// Put this file in any folder named "Editor", e.g. Assets/Editor/AutoAddressableSprites.cs
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public class AutoAddressableSprites : AssetPostprocessor
{
    // ---- CONFIG: edit this list ----
    // Each entry = (folder to watch, Addressables group name).
    // Use "" as the group name to use the default group.
    // Subfolders are included. If folders overlap, the first match wins.
    static readonly (string folder, string group)[] Targets =
    {
        ("Assets/Map/Scripts/Missions/Photos", "MissionImages"),
        ("Assets/Map/Scripts/MiniGames/Puzzle/BaseImages", "PuzzleBaseImages"),
        ("Assets/Map/Scripts/MiniGames/Puzzle/PieceImages", "PuzzlePieces"),
    };
    // --------------------------------

    static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".psd" };

    // Returns true if the path is an image inside one of the target folders.
    static bool TryGetTarget(string path, out string group)
    {
        group = "";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (System.Array.IndexOf(ImageExtensions, ext) < 0) return false;

        foreach (var t in Targets)
        {
            if (path.StartsWith(t.folder.TrimEnd('/') + "/"))
            {
                group = t.group;
                return true;
            }
        }
        return false;
    }

    // Runs before a texture is imported: set Sprite (2D and UI) + Single.
    // Only applies on the FIRST import, so later manual tweaks are not overwritten.
    void OnPreprocessTexture()
    {
        if (!TryGetTarget(assetPath, out _)) return;
        if (!assetImporter.importSettingsMissing) return;

        ApplyImporterSettings((TextureImporter)assetImporter);
    }

    // Runs after assets are imported or moved: register them in Addressables.
    static void OnPostprocessAllAssets(
        string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths)
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null) return; // Addressables not initialised yet

        bool changed = false;
        foreach (string path in importedAssets)
            if (TryGetTarget(path, out string group)) changed |= Register(settings, path, group);
        foreach (string path in movedAssets)
            if (TryGetTarget(path, out string group)) changed |= Register(settings, path, group);

        if (changed) AssetDatabase.SaveAssets();
    }

    static void ApplyImporterSettings(TextureImporter importer)
    {
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
    }

    // Marks the asset Addressable with address = file name (no extension).
    // Skips assets that are already Addressable, so existing custom addresses are kept.
    static bool Register(AddressableAssetSettings settings, string path, string groupName)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (settings.FindAssetEntry(guid) != null) return false;

        AddressableAssetGroup group = string.IsNullOrEmpty(groupName)
            ? settings.DefaultGroup
            : settings.FindGroup(groupName);

        if (group == null)
        {
            Debug.LogWarning($"Addressables group '{groupName}' not found; using default group for {path}");
            group = settings.DefaultGroup;
        }

        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = Path.GetFileNameWithoutExtension(path);
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryCreated, entry, true, true);
        return true;
    }

    // One-click fix for images that are already in the folders.
    [MenuItem("Tools/Addressables/Apply Sprite Settings To Target Folders")]
    static void ApplyToExisting()
    {
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("Addressables settings not found. Create them via Window > Asset Management > Addressables > Groups.");
            return;
        }

        var folders = new System.Collections.Generic.List<string>();
        foreach (var t in Targets)
        {
            if (AssetDatabase.IsValidFolder(t.folder)) folders.Add(t.folder);
            else Debug.LogWarning($"Target folder not found, skipping: {t.folder}");
        }
        if (folders.Count == 0) return;

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", folders.ToArray());
        int count = 0;

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!TryGetTarget(path, out string group)) continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    ApplyImporterSettings(importer);
                    importer.SaveAndReimport();
                }
                Register(settings, path, group);
                count++;
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Processed {count} images in {folders.Count} folder(s)");
    }
}
