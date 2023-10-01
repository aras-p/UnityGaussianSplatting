using UnityEngine;

// I'd use TextAsset, but that one (as of Unity 2022) does not have a constructor that
// takes a byte array :(
[PreferBinarySerialization]
public class GaussianSplatAssetByteBuffer : ScriptableObject
{
    [HideInInspector] public byte[] bytes;

    public static GaussianSplatAssetByteBuffer CreateContainer(byte[] bytes)
    {
        var container = ScriptableObject.CreateInstance<GaussianSplatAssetByteBuffer>();
        container.bytes = bytes;
        return container;
    }
}
