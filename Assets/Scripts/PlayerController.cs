using UnityEngine;

namespace ArcaneDrift
{
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private Camera cam;
        [SerializeField] private ClickMarker clickMarkerPrefab;

        [SerializeField] private float moveSpeed = 2.5f;
        [SerializeField] private float stopDistance = 0.02f;

        private bool hasMoveTarget;
        private Vector2 moveTarget;

        public bool IsMoving { get; private set; }
        public Vector2 Facing { get; private set; } = Vector2.down;

        private void Update()
        {
            if (Input.GetMouseButtonDown(1))
            {
                moveTarget = CursorWorld();
                hasMoveTarget = true;

                if (clickMarkerPrefab != null)
                {
                    Instantiate(clickMarkerPrefab, moveTarget, Quaternion.identity);
                }
            }

            MoveTowardsTarget();
        }

        private void MoveTowardsTarget()
        {
            if (!hasMoveTarget)
            {
                IsMoving = false;
                return;
            }

            Vector2 current = transform.position;
            Vector2 toTarget = moveTarget - current;
            float distance = toTarget.magnitude;

            if (distance <= stopDistance)
            {
                hasMoveTarget = false;
                IsMoving = false;
                return;
            }

            Vector2 direction = toTarget / distance;
            Facing = direction;
            IsMoving = true;

            float step = moveSpeed * Time.deltaTime;
            if (step >= distance)
            {
                transform.position = moveTarget;
                hasMoveTarget = false;
                IsMoving = false;
            }
            else
            {
                transform.position = current + direction * step;
            }
        }

        // 화면 좌표 -> 월드 좌표(z=0 평면). 카메라가 Orthographic이라 z 거리는 무관하지만
        // ScreenToWorldPoint가 요구하므로 카메라와의 z 거리를 넣어준다.
        public Vector2 CursorWorld()
        {
            Vector3 screenPoint = Input.mousePosition;
            screenPoint.z = -cam.transform.position.z;
            return cam.ScreenToWorldPoint(screenPoint);
        }

        public void ClearMoveTarget()
        {
            hasMoveTarget = false;
            IsMoving = false;
        }
    }
}
