using UnityEngine;

namespace ArcaneDrift
{
    // STEP 1: 우클릭한 지점으로 이동. 물리(Rigidbody2D) 없이 transform.position 을 직접 옮긴다.
    // STEP 3에서 "적 위 우클릭 = 추격 + 자동 평타"가 여기에 추가된다.
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] Camera cam;
        [SerializeField] ClickMarker clickMarkerPrefab;   // 비워 두면 클릭 표시 없이 이동만 한다
        [SerializeField] float moveSpeed = 2.5f;          // 원본 250px/s ÷ 100
        [SerializeField] float radius = 0.15f;            // 원본 15px ÷ 100

        Vector2? moveTarget;

        public bool IsMoving => moveTarget.HasValue;
        public Vector2 Facing { get; private set; } = Vector2.right;
        public float Radius => radius;

        void Awake()
        {
            if (cam == null) cam = Camera.main;
        }

        void Update()
        {
            if (GameInput.RightClickDown())
            {
                Vector2 point = CursorWorld();
                moveTarget = point;
                if (clickMarkerPrefab != null) Instantiate(clickMarkerPrefab, point, Quaternion.identity);
            }

            if (moveTarget.HasValue) MoveTowardTarget(moveTarget.Value);
        }

        void MoveTowardTarget(Vector2 target)
        {
            Vector2 pos = transform.position;
            Vector2 toTarget = target - pos;
            float dist = toTarget.magnitude;
            float step = moveSpeed * Time.deltaTime;

            if (dist <= step)
            {
                SetPosition(target);
                moveTarget = null;
                return;
            }
            Facing = toTarget / dist;
            SetPosition(pos + Facing * step);
        }

        // z 값은 건드리지 않는다 (카메라보다 앞에 있어야 보임)
        void SetPosition(Vector2 p) => transform.position = new Vector3(p.x, p.y, transform.position.z);

        public Vector2 CursorWorld()
        {
            Vector3 screen = GameInput.MousePosition();
            screen.z = -cam.transform.position.z;
            return cam.ScreenToWorldPoint(screen);
        }

        public void ClearMoveTarget() => moveTarget = null;
    }
}
