using NativeCompressions.LZ4;
using UnityEngine;

public class NewMonoBehaviourScript : MonoBehaviour
{
    void Start()
    {
        Debug.Log(LZ4.Version);
    }
}
