using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    [ScriptedImporter(1,"gasrefcheck")]
    internal sealed class EffectFixtureImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx){
            string source=File.ReadAllText(ctx.assetPath);
            ctx.DependsOnSourceAsset(source);
            var fixture=UnityEngine.ScriptableObject.CreateInstance<EffectReferenceFixture>();
            fixture.effect=AssetDatabase.LoadAllAssetsAtPath(source).OfType<GameplayEffectAsset>().First();
            ctx.AddObjectToAsset("fixture",fixture);ctx.SetMainObject(fixture);
        }
    }
}
