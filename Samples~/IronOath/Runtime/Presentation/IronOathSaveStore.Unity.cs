using UnityEngine;

namespace IronOath.Presentation
{
    internal sealed partial class IronOathSaveStore
    {
        internal IronOathSaveStore(string directory) : this(directory, Serialize, Deserialize) { }
        static string Serialize(IronOathSaveData data) => JsonUtility.ToJson(data);
        static IronOathSaveData Deserialize(string json) => JsonUtility.FromJson<IronOathSaveData>(json);
    }
}
