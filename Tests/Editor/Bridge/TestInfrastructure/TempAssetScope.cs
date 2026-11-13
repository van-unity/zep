using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.TestInfrastructure
{
    // Owns a throwaway folder under Assets for tests that need real,
    // AssetDatabase-visible fixtures; Dispose removes the folder and
    // everything created inside it.
    internal sealed class TempAssetScope : IDisposable
    {
        internal TempAssetScope()
        {
            FolderPath = "Assets/TempAssetScope_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(FolderPath));
        }

        internal string FolderPath { get; }

        internal string CreateTextAsset(string fileName, string content)
        {
            var assetPath = FolderPath + "/" + fileName;
            File.WriteAllText(assetPath, content);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        internal T CreateScriptableObjectAsset<T>(string fileName) where T : ScriptableObject
        {
            var assetPath = FolderPath + "/" + fileName;
            var instance = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(instance, assetPath);
            return instance;
        }

        public void Dispose()
        {
            AssetDatabase.DeleteAsset(FolderPath);
        }
    }
}
