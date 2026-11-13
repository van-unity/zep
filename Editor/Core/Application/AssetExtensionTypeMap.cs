using System;
using System.Collections.Generic;

namespace Zep.Core.Application
{

public static class AssetExtensionTypeMap
{
	public const string UnknownTypeName = "Unknown";

	public const string ScriptableObjectTypeName = "ScriptableObject";

	private static readonly Dictionary<string, string> Map = BuildMap();

	internal static bool TryGet(string extension, out string typeName)
	{
		if (string.IsNullOrEmpty(extension))
		{
			typeName = null;
			return false;
		}
		return Map.TryGetValue(extension, out typeName);
	}

	public static string GetTypeNameForPath(string assetPath)
	{
		if (string.IsNullOrEmpty(assetPath))
		{
			return "Unknown";
		}
		string lowerExtension = GetLowerExtension(assetPath);
		if (string.IsNullOrEmpty(lowerExtension))
		{
			return "Unknown";
		}
		if (lowerExtension == ".asset")
		{
			return "ScriptableObject";
		}
		string value;
		return Map.TryGetValue(lowerExtension, out value) ? value : "Unknown";
	}

	public static string GetLowerExtension(string assetPath)
	{
		if (string.IsNullOrEmpty(assetPath))
		{
			return string.Empty;
		}
		int dotIndex = assetPath.LastIndexOf('.');
		if (dotIndex < 0 || dotIndex == assetPath.Length - 1)
		{
			return string.Empty;
		}
		int lastSeparator = assetPath.LastIndexOf('/');
		if (lastSeparator > dotIndex)
		{
			return string.Empty;
		}
		int extensionLength = assetPath.Length - dotIndex;
		char[] extension = new char[extensionLength];
		for (int i = 0; i < extensionLength; i++)
		{
			char character = assetPath[dotIndex + i];
			extension[i] = ((character >= 'A' && character <= 'Z') ? ((char)(character + 32)) : character);
		}
		return new string(extension);
	}

	private static Dictionary<string, string> BuildMap()
	{
		Dictionary<string, string> typeNamesByExtension = new Dictionary<string, string>(StringComparer.Ordinal);
		typeNamesByExtension[".cs"] = "MonoScript";
		typeNamesByExtension[".asmdef"] = "AssemblyDefinitionAsset";
		typeNamesByExtension[".asmref"] = "AssemblyDefinitionReferenceAsset";
		typeNamesByExtension[".prefab"] = "GameObject";
		typeNamesByExtension[".unity"] = "SceneAsset";
		typeNamesByExtension[".mat"] = "Material";
		typeNamesByExtension[".shader"] = "Shader";
		typeNamesByExtension[".compute"] = "ComputeShader";
		typeNamesByExtension[".controller"] = "AnimatorController";
		typeNamesByExtension[".overridecontroller"] = "AnimatorOverrideController";
		typeNamesByExtension[".anim"] = "AnimationClip";
		typeNamesByExtension[".mask"] = "AvatarMask";
		typeNamesByExtension[".playable"] = "TimelineAsset";
		typeNamesByExtension[".mixer"] = "AudioMixer";
		typeNamesByExtension[".spriteatlas"] = "SpriteAtlas";
		typeNamesByExtension[".physicmaterial"] = "PhysicMaterial";
		typeNamesByExtension[".physicsmaterial2d"] = "PhysicsMaterial2D";
		typeNamesByExtension[".preset"] = "Preset";
		typeNamesByExtension[".guiskin"] = "GUISkin";
		typeNamesByExtension[".fontsettings"] = "Font";
		typeNamesByExtension[".cubemap"] = "Cubemap";
		typeNamesByExtension[".flare"] = "Flare";
		typeNamesByExtension[".rendertexture"] = "RenderTexture";
		typeNamesByExtension[".lighting"] = "LightingSettings";
		typeNamesByExtension[".shadervariants"] = "ShaderVariantCollection";
		typeNamesByExtension[".uxml"] = "VisualTreeAsset";
		typeNamesByExtension[".uss"] = "StyleSheet";
		typeNamesByExtension[".tss"] = "ThemeStyleSheet";
		typeNamesByExtension[".inputactions"] = "InputActionAsset";
		typeNamesByExtension[".png"] = "Texture2D";
		typeNamesByExtension[".jpg"] = "Texture2D";
		typeNamesByExtension[".jpeg"] = "Texture2D";
		typeNamesByExtension[".tga"] = "Texture2D";
		typeNamesByExtension[".bmp"] = "Texture2D";
		typeNamesByExtension[".tif"] = "Texture2D";
		typeNamesByExtension[".tiff"] = "Texture2D";
		typeNamesByExtension[".psd"] = "Texture2D";
		typeNamesByExtension[".gif"] = "Texture2D";
		typeNamesByExtension[".exr"] = "Texture2D";
		typeNamesByExtension[".hdr"] = "Texture2D";
		typeNamesByExtension[".iff"] = "Texture2D";
		typeNamesByExtension[".pict"] = "Texture2D";
		typeNamesByExtension[".fbx"] = "GameObject";
		typeNamesByExtension[".obj"] = "GameObject";
		typeNamesByExtension[".blend"] = "GameObject";
		typeNamesByExtension[".dae"] = "GameObject";
		typeNamesByExtension[".3ds"] = "GameObject";
		typeNamesByExtension[".dxf"] = "GameObject";
		typeNamesByExtension[".skp"] = "GameObject";
		typeNamesByExtension[".wav"] = "AudioClip";
		typeNamesByExtension[".mp3"] = "AudioClip";
		typeNamesByExtension[".ogg"] = "AudioClip";
		typeNamesByExtension[".aiff"] = "AudioClip";
		typeNamesByExtension[".aif"] = "AudioClip";
		typeNamesByExtension[".mod"] = "AudioClip";
		typeNamesByExtension[".it"] = "AudioClip";
		typeNamesByExtension[".s3m"] = "AudioClip";
		typeNamesByExtension[".xm"] = "AudioClip";
		typeNamesByExtension[".mp4"] = "VideoClip";
		typeNamesByExtension[".mov"] = "VideoClip";
		typeNamesByExtension[".webm"] = "VideoClip";
		typeNamesByExtension[".m4v"] = "VideoClip";
		typeNamesByExtension[".avi"] = "VideoClip";
		typeNamesByExtension[".ttf"] = "Font";
		typeNamesByExtension[".otf"] = "Font";
		typeNamesByExtension[".txt"] = "TextAsset";
		typeNamesByExtension[".json"] = "TextAsset";
		typeNamesByExtension[".xml"] = "TextAsset";
		typeNamesByExtension[".yaml"] = "TextAsset";
		typeNamesByExtension[".yml"] = "TextAsset";
		typeNamesByExtension[".md"] = "TextAsset";
		typeNamesByExtension[".html"] = "TextAsset";
		typeNamesByExtension[".htm"] = "TextAsset";
		typeNamesByExtension[".csv"] = "TextAsset";
		typeNamesByExtension[".bytes"] = "TextAsset";
		typeNamesByExtension[".cginc"] = "ShaderInclude";
		typeNamesByExtension[".hlsl"] = "ShaderInclude";
		typeNamesByExtension[".glslinc"] = "ShaderInclude";
		typeNamesByExtension[".dll"] = "DefaultAsset";
		typeNamesByExtension[".so"] = "DefaultAsset";
		typeNamesByExtension[".dylib"] = "DefaultAsset";
		// Plugin bundles are DIRECTORIES that Unity imports as one asset, so the panel
		// types them by suffix like any other file. GetMainAssetTypeAtPath reports
		// DefaultAsset for all four on 6000.3.10f1.
		typeNamesByExtension[".bundle"] = "DefaultAsset";
		typeNamesByExtension[".framework"] = "DefaultAsset";
		typeNamesByExtension[".xcframework"] = "DefaultAsset";
		typeNamesByExtension[".plugin"] = "DefaultAsset";
		return typeNamesByExtension;
	}
}

}
