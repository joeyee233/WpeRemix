using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WpeRemix.Core;

public sealed class SceneComposer : ISceneComposer
{
    private const long MaxPixels = 100_000_000;
    private const long MaxJsonBytes = 32 * 1024 * 1024;
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    public Task<CompositionResult> ComposeAsync(string extractedDirectory, string destinationFile, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CompositionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Compose(extractedDirectory, destinationFile, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.IsBackground = true;
        thread.Name = "WpeRemix renderer";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static CompositionResult Compose(string root, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var scenePath = Path.Combine(root, "scene.json");
        if (!File.Exists(scenePath))
        {
            var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => !Path.GetRelativePath(root, path).Replace('\\', '/').Contains("/masks/", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileNameWithoutExtension(path).Equals("preview", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidDataException("没有找到可用的 scene.json，且图片不止一张或没有可解码图片。请查看已提取的素材资源");
            }
            var source = LoadBitmap(candidates[0], warnings);
            SavePng(source, destination, cancellationToken);
            return new CompositionResult(destination, warnings.ToArray());
        }

        using var sceneDocument = ReadJson(scenePath);
        var scene = sceneDocument.RootElement;
        var general = Get(scene, "general");
        var projection = Get(general, "orthogonalprojection");
        if (projection.ValueKind != JsonValueKind.Object)
        {
            throw new NotSupportedException("这不是受支持的 2D 正交场景，暂不能合成 3D 或透视场景。可查看已提取的场景素材");
        }
        var width = CheckedDimension(Number(Get(projection, "width"), 0));
        var height = CheckedDimension(Number(Get(projection, "height"), 0));
        CheckPixels(width, height);
        var nodes = Get(scene, "objects");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 20000)
        {
            throw new InvalidDataException("场景图层定义无效或超过 20000 层限制");
        }
        var objectList = nodes.EnumerateArray().ToArray();
        var lookup = new Dictionary<long, JsonElement>();
        foreach (var node in objectList)
        {
            var id = Get(node, "id");
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var objectId))
            {
                if (!lookup.TryAdd(objectId, node))
                {
                    throw new InvalidDataException("场景含重复图层编号");
                }
            }
        }
        using var projectDocument = ReadOptionalJson(Path.Combine(root, "project.json"));
        var properties = default(JsonElement);
        if (projectDocument != null)
        {
            properties = Get(Get(projectDocument.RootElement, "general"), "properties");
        }
        var visual = new DrawingVisual();
        var layerCount = 0;
        var decodeBudget = new DecodeBudget { CanvasPixels = (long)width * height };
        var state = new SceneState(lookup, properties, warnings);
        using (var context = visual.RenderOpen())
        {
            var background = Colors.Transparent;
            if (Boolean(Get(general, "clearenabled"), true))
            {
                background = ParseColor(Value(Get(general, "clearcolor"), properties, warnings), Colors.Black);
            }
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            foreach (var node in objectList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsVisible(node, state, new HashSet<long>()))
                {
                    continue;
                }
                var imageReference = Text(Get(node, "image"));
                var name = Text(Get(node, "name"));
                if (string.IsNullOrWhiteSpace(imageReference))
                {
                    if (Get(node, "particle").ValueKind != JsonValueKind.Undefined || Get(node, "text").ValueKind != JsonValueKind.Undefined || Get(node, "font").ValueKind != JsonValueKind.Undefined)
                    {
                        warnings.Add("文字、时钟和粒子不参与静态原图合成");
                    }
                    continue;
                }
                if (imageReference.Contains("/util/", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("已跳过程序生成图层（音频条、时钟合成层或纯色效果层）");
                    continue;
                }
                var alignment = Text(Get(node, "alignment"));
                if (alignment.Length > 0 && !alignment.Equals("center", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException($"图层 {name} 使用非居中锚点，暂不能准确合成。请查看已提取的素材");
                }
                if (Get(node, "instance").ValueKind != JsonValueKind.Undefined || Get(node, "usertextures").ValueKind != JsonValueKind.Undefined)
                {
                    warnings.Add($"图层 {name} 含实例或用户纹理覆盖，当前使用包内基础材质");
                }
                if (Number(Get(node, "colorBlendMode"), 0) != 0)
                {
                    warnings.Add($"已跳过特殊混合图层：{name}，需 Wallpaper Engine 的着色器渲染");
                    continue;
                }
                var modelPath = SafePath(root, imageReference);
                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException($"缺少模型资源：{imageReference}");
                }
                using var modelDocument = ReadJson(modelPath);
                var model = modelDocument.RootElement;
                var materialReference = Text(Get(model, "material"));
                if (string.IsNullOrWhiteSpace(materialReference))
                {
                    warnings.Add($"图层 {name} 没有可用的静态图片材质，已跳过");
                    continue;
                }
                using var materialDocument = ReadJson(SafePath(root, materialReference));
                var passes = Get(materialDocument.RootElement, "passes");
                if (passes.ValueKind != JsonValueKind.Array || passes.GetArrayLength() == 0)
                {
                    throw new InvalidDataException($"图层 {name} 的材质没有渲染通道");
                }
                if (passes.GetArrayLength() > 1)
                {
                    warnings.Add($"图层 {name} 使用多个材质通道，仅保留基础图片");
                }
                var pass = passes[0];
                var shader = Text(Get(pass, "shader"));
                var blend = Text(Get(pass, "blending"));
                if (!shader.StartsWith("genericimage", StringComparison.OrdinalIgnoreCase) || (blend.Length > 0 && blend != "translucent" && blend != "normal" && blend != "opaque"))
                {
                    warnings.Add($"已跳过自定义着色器或特殊混合材质：{name}");
                    continue;
                }
                var textures = Get(pass, "textures");
                if (textures.ValueKind != JsonValueKind.Array || textures.GetArrayLength() == 0 || textures[0].ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException($"图层 {name} 缺少基础纹理");
                }
                var textureReference = textures[0].GetString() ?? string.Empty;
                var texturePath = FindTexture(root, textureReference);
                var bitmap = LoadBitmap(texturePath, warnings, decodeBudget);
                var size = Vector(Value(Get(node, "size"), properties, warnings), new double[] { bitmap.PixelWidth, bitmap.PixelHeight, 0 });
                if (size[0] <= 0 || size[1] <= 0 || size[0] > 100000 || size[1] > 100000)
                {
                    throw new InvalidDataException($"图层 {name} 的尺寸无效");
                }
                var alpha = Math.Clamp(Number(Value(Get(node, "alpha"), properties, warnings), 1), 0, 1);
                var color = Vector(Value(Get(node, "color"), properties, warnings), new double[] { 1, 1, 1 });
                var brightness = Number(Value(Get(node, "brightness"), properties, warnings), 1);
                bitmap = ApplyColor(bitmap, color, brightness, cancellationToken);
                if (Get(model, "puppet").ValueKind != JsonValueKind.Undefined || Get(node, "animationlayers").ValueKind != JsonValueKind.Undefined)
                {
                    warnings.Add("骨骼动画使用纹理默认姿态，裁切偏移和姿势可能与播放中的画面不同");
                }
                if (Get(node, "effects").ValueKind == JsonValueKind.Array && Get(node, "effects").GetArrayLength() > 0)
                {
                    warnings.Add("动态滤镜、光效、涟漪和脚本效果未渲染，输出为基础图层合成");
                }
                var matrix = WorldMatrix(node, state, new HashSet<long>());
                matrix.Append(new Matrix(1, 0, 0, -1, 0, height));
                var rectangle = new Rect(-size[0] / 2, -size[1] / 2, size[0], size[1]);
                context.PushTransform(new MatrixTransform(matrix));
                context.PushOpacity(alpha);
                // Local scene coordinates point upward; flip the bitmap within its own layer.
                context.PushTransform(new ScaleTransform(1, -1));
                context.DrawImage(bitmap, rectangle);
                context.Pop();
                context.Pop();
                context.Pop();
                layerCount++;
            }
        }
        if (layerCount == 0)
        {
            throw new NotSupportedException("未找到可合成的静态图片图层，请查看已提取的素材资源");
        }
        var renderTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        renderTarget.Render(visual);
        renderTarget.Freeze();
        SavePng(renderTarget, destination, cancellationToken);
        return new CompositionResult(destination, warnings.ToArray());
    }

    private static Matrix WorldMatrix(JsonElement node, SceneState state, HashSet<long> visited)
    {
        var origin = Vector(Value(Get(node, "origin"), state.Properties, state.Warnings), new double[] { 0, 0, 0 });
        var scale = Vector(Value(Get(node, "scale"), state.Properties, state.Warnings), new double[] { 1, 1, 1 });
        var angles = Vector(Value(Get(node, "angles"), state.Properties, state.Warnings), new double[] { 0, 0, 0 });
        if (Math.Abs(angles[0]) > 0.0001 || Math.Abs(angles[1]) > 0.0001)
        {
            throw new NotSupportedException("场景含 X/Y 轴三维旋转，暂不能准确合成。请查看已提取的素材");
        }
        var matrix = Matrix.Identity;
        matrix.Scale(scale[0], scale[1]);
        matrix.Rotate(angles[2] * 180 / Math.PI);
        matrix.Translate(origin[0], origin[1]);
        var parent = Get(node, "parent");
        if (parent.ValueKind == JsonValueKind.Number && parent.TryGetInt64(out var parentId))
        {
            if (!visited.Add(parentId) || visited.Count > 128)
            {
                throw new InvalidDataException("图层父级循环引用或嵌套过深");
            }
            if (!state.Objects.TryGetValue(parentId, out var parentNode))
            {
                throw new InvalidDataException("找不到图层父级");
            }
            matrix.Append(WorldMatrix(parentNode, state, visited));
        }
        return matrix;
    }

    private static bool IsVisible(JsonElement node, SceneState state, HashSet<long> visited)
    {
        if (!Boolean(Value(Get(node, "visible"), state.Properties, state.Warnings), true))
        {
            return false;
        }
        var parent = Get(node, "parent");
        if (parent.ValueKind == JsonValueKind.Number && parent.TryGetInt64(out var id))
        {
            if (!visited.Add(id) || visited.Count > 128)
            {
                throw new InvalidDataException("图层父级循环引用或嵌套过深");
            }
            if (state.Objects.TryGetValue(id, out var parentNode))
            {
                return IsVisible(parentNode, state, visited);
            }
        }
        return true;
    }

    private static JsonElement Value(JsonElement element, JsonElement properties, HashSet<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return element;
        }
        var user = Get(element, "user");
        if (user.ValueKind == JsonValueKind.String)
        {
            var property = Get(properties, user.GetString() ?? string.Empty);
            var propertyValue = Get(property, "value");
            var fallback = Get(element, "value");
            if (propertyValue.ValueKind == fallback.ValueKind && propertyValue.ValueKind != JsonValueKind.Undefined)
            {
                return propertyValue;
            }
        }
        if (user.ValueKind == JsonValueKind.Object || Get(element, "script").ValueKind != JsonValueKind.Undefined)
        {
            warnings.Add("脚本和条件表达式使用资源包中保存的默认值，未执行脚本");
        }
        return Get(element, "value");
    }

    private static BitmapSource LoadBitmap(string path, HashSet<string> warnings)
    {
        return LoadBitmap(path, warnings, new DecodeBudget());
    }

    private static BitmapSource LoadBitmap(string path, HashSet<string> warnings, DecodeBudget budget)
    {
        if (new FileInfo(path).Length > 512L * 1024 * 1024)
        {
            throw new InvalidDataException("单张图片超过 512 MB 限制");
        }
        using var stream = File.OpenRead(path);
        var headerDecoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
        if (headerDecoder.Frames.Count == 0)
        {
            throw new InvalidDataException("图片没有可解码帧");
        }
        CheckPixels(headerDecoder.Frames[0].PixelWidth, headerDecoder.Frames[0].PixelHeight);
        budget.DecodedPixels = checked(budget.DecodedPixels + (long)headerDecoder.Frames[0].PixelWidth * headerDecoder.Frames[0].PixelHeight);
        if (budget.DecodedPixels + budget.CanvasPixels * 2 > 220_000_000)
        {
            throw new InvalidDataException("该场景解码后的累计像素超出内存预算，请查看已提取的素材分别处理");
        }
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("图片没有可解码帧");
        }
        var frame = decoder.Frames[0];
        CheckPixels(frame.PixelWidth, frame.PixelHeight);
        if (decoder.Frames.Count > 1)
        {
            warnings.Add("动画纹理仅使用第一帧");
        }
        frame.Freeze();
        return frame;
    }

    private static BitmapSource ApplyColor(BitmapSource bitmap, double[] color, double brightness, CancellationToken cancellationToken)
    {
        if (color.All(value => Math.Abs(value - 1) < 0.00001) && Math.Abs(brightness - 1) < 0.00001)
        {
            return bitmap;
        }
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var index = y * stride + x * 4;
                pixels[index] = (byte)Math.Clamp(pixels[index] * color[2] * brightness, 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(pixels[index + 1] * color[1] * brightness, 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(pixels[index + 2] * color[0] * brightness, 0, 255);
            }
        }
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static string FindTexture(string root, string reference)
    {
        var normalized = reference.Replace('\\', '/');
        if (!normalized.StartsWith("materials/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "materials/" + normalized;
        }
        if (ImageExtensions.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase))
        {
            var direct = SafePath(root, normalized);
            if (File.Exists(direct))
            {
                return direct;
            }
        }
        if (Path.GetExtension(normalized).Equals(".tex", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        foreach (var extension in ImageExtensions)
        {
            var path = SafePath(root, normalized + extension);
            if (File.Exists(path))
            {
                return path;
            }
        }
        throw new FileNotFoundException($"基础纹理没有成功转换或依赖未打包：{reference}");
    }

    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InvalidDataException("场景含非法资源路径");
        }
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("场景资源路径超出解包目录");
        }
        return path;
    }

    private static void SavePng(BitmapSource source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void CheckPixels(int width, int height)
    {
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || (long)width * height > MaxPixels)
        {
            throw new InvalidDataException("图片尺寸超出限制（单边 32768 像素，总计 1 亿像素）");
        }
    }

    private static int CheckedDimension(double value)
    {
        if (!double.IsFinite(value) || value < 1 || value > 32768 || Math.Abs(value - Math.Round(value)) > 0.001)
        {
            throw new InvalidDataException("场景画布尺寸无效");
        }
        return checked((int)Math.Round(value));
    }

    private static JsonDocument ReadJson(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxJsonBytes)
        {
            throw new InvalidDataException($"JSON 资源不存在或超过 32 MB：{file.Name}");
        }
        using var stream = file.OpenRead();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }

    private static JsonDocument? ReadOptionalJson(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return ReadJson(path);
    }

    private static JsonElement Get(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value))
        {
            return value;
        }
        return default;
    }

    private static string Text(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static double Number(JsonElement element, double fallback)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return number;
        }
        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
        {
            return number;
        }
        return fallback;
    }

    private static bool Boolean(JsonElement element, bool fallback)
    {
        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }
        if (element.ValueKind == JsonValueKind.False)
        {
            return false;
        }
        return fallback;
    }

    private static double[] Vector(JsonElement element, double[] fallback)
    {
        var output = (double[])fallback.Clone();
        var components = Text(element).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < Math.Min(3, components.Length); index++)
        {
            if (!double.TryParse(components[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || Math.Abs(value) > 10000000)
            {
                throw new InvalidDataException("图层变换参数无效");
            }
            output[index] = value;
        }
        return output;
    }

    private static Color ParseColor(JsonElement element, Color fallback)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }
        var rgb = Vector(element, new[] { fallback.R / 255.0, fallback.G / 255.0, fallback.B / 255.0 });
        return Color.FromRgb((byte)Math.Round(Math.Clamp(rgb[0], 0, 1) * 255), (byte)Math.Round(Math.Clamp(rgb[1], 0, 1) * 255), (byte)Math.Round(Math.Clamp(rgb[2], 0, 1) * 255));
    }

    private sealed record SceneState(Dictionary<long, JsonElement> Objects, JsonElement Properties, HashSet<string> Warnings);

    private sealed class DecodeBudget
    {
        public long CanvasPixels { get; init; }
        public long DecodedPixels { get; set; }
    }
}
