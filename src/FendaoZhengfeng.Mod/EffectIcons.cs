using Godot;

namespace FendaoZhengfeng.Mod;

/// <summary>Hand-painted PNG resources travel inside the DLL, including the native modifier icon.</summary>
internal static class EffectIcons
{
    private static readonly Dictionary<string, Texture2D> textures = [];
    internal const int TextureSize = 128;
    public static Texture2D Mode => Get("mode_icon");
    public static Texture2D Get(string id)
    {
        if (textures.TryGetValue(id, out var texture)) return texture;
        string resource = "FendaoZhengfeng.Assets." + id + ".png";
        using var source = typeof(EffectIcons).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("缺少随包图标资源：" + id);
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        var image = new Image();
        if (image.LoadPngFromBuffer(bytes.ToArray()) != Error.Ok) throw new InvalidOperationException("图标加载失败：" + id);
        // Preserve the original artwork; downsample only the GPU display texture for the small HUD.
        image.Resize(TextureSize, TextureSize, Image.Interpolation.Lanczos);
        texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = id;
        textures[id] = texture;
        return texture;
    }
}
