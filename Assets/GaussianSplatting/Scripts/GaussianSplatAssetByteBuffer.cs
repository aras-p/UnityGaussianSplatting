using UnityEngine;

// I'd use TextAsset, but that one (as of Unity 2022) does not have a constructor that
// takes a byte array :(
//
// I'd also use a byte[] directly in the GaussianSplatAsset, but large arrays inside
// objects make the inspector *really* slow (as of Unity 2022), even if you're using
// custom inspector that does not need to display said arrays.
//
// And then, we don't use byte[] because that will make any domain reload quite slow
// because Unity will call SerializableManagedRefsUtilities::RestoreBackups as part
// of "hey let's reimport any changed assets" work, which will serialize or
// de-serialize various managed objects. And doing that byte-by-byte is kinda slow,
// so at least do it long-by-long.
[PreferBinarySerialization]
public class GaussianSplatAssetByteBuffer : ScriptableObject
{
    [HideInInspector] public ulong[] data;

    public static GaussianSplatAssetByteBuffer CreateContainer(ulong[] data)
    {
        var container = ScriptableObject.CreateInstance<GaussianSplatAssetByteBuffer>();
        container.data = data;
        return container;
    }
}
