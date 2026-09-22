using UnityEngine;

namespace ArcaneDrift
{
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;

        private void LateUpdate()
        {
            if (target == null) return;

            Vector3 pos = transform.position;
            pos.x = target.position.x;
            pos.y = target.position.y;
            transform.position = pos;
        }
    }
}
