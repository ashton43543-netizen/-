using UnityEngine;

namespace ArcaneDrift
{
    // 플레이어를 따라가는 카메라. 플레이어가 Update 에서 움직인 뒤에 따라가야 떨리지 않으므로 LateUpdate 를 쓴다.
    [RequireComponent(typeof(Camera))]
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] Transform target;
        [SerializeField] float orthographicSize = 4.5f;

        void Awake()
        {
            var cam = GetComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = orthographicSize;
        }

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 p = target.position;
            transform.position = new Vector3(p.x, p.y, transform.position.z);
        }
    }
}
