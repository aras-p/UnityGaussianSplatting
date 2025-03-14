using UnityEngine;

public class PlayerMovement : MonoBehaviour
{
    // 玩家移动速度
    public float moveSpeed = 5f;
    // 鼠标灵敏度
    public float mouseSensitivity = 100f;
    // 摄像机 Transform
    public Transform cameraTransform;
    // 摄像机上下旋转角
    private float xRotation = 0f;

    void Start()
    {
        // 锁定鼠标
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Update()
    {
        // 鼠标控制视角
        HandleMouseLook();

        // 键盘控制移动
        HandleMovement();
    }

    void HandleMouseLook()
    {
        // 获取鼠标输入
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        // 控制摄像机上下旋转
        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f); // 限制旋转角度
        cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        // 控制玩家左右旋转
        transform.Rotate(Vector3.up * mouseX);
    }

    void HandleMovement()
    {
        // 获取键盘输入（WASD）
        float moveX = Input.GetAxis("Horizontal"); // 左右
        float moveZ = Input.GetAxis("Vertical");    // 前后

        // 计算移动方向（基于玩家的本地坐标系）
        Vector3 move = transform.right * moveX + transform.forward * moveZ;

        // 平移玩家
        transform.Translate(move * moveSpeed * Time.deltaTime);
    }
}