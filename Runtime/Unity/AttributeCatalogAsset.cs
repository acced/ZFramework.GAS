using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class AttributeIdFieldAttribute : PropertyAttribute { }

    [Serializable]
    public sealed class AttributeCatalogEntry
    {
        public int id;
        public string displayName;
        public string category;
        public string Label => string.IsNullOrEmpty(category) ? displayName : category + " / " + displayName;
    }

    [CreateAssetMenu(menuName = "ZFramework GAS/Attribute Catalog", fileName = "AttributeCatalog")]
    public sealed class AttributeCatalogAsset : ScriptableObject
    {
        public AttributeCatalogEntry[] entries = Array.Empty<AttributeCatalogEntry>();
        public AttributeCatalogEntry Find(int id) => Array.Find(entries, item => item != null && item.id == id);
    }

}
