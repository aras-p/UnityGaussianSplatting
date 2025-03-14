using UnityEngine;

public class ShipMovement : MonoBehaviour
{
    // 船的移动速度
    public float moveSpeed = 5f;

    void Update()
    {
        // 让船沿着其自身的 Z 轴正方向（通常是向前）移动
        transform.Translate(Vector3.left * moveSpeed * Time.deltaTime);
    }
}