using UnityEngine;

// I'd use TextAsset, but that one (as of Unity 2022) does not have a constructor that
// takes a byte array :(
//
// I'd also use a byte[] directly in the GaussianSplatAsset, but large arrays inside
// objects make the inspector *really* slow (as of Unity 2022), even if you're using
// custom inspector that does not need to display said arrays.
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
