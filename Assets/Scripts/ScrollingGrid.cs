using UnityEngine;

namespace ArcaneDrift
{
    [RequireComponent(typeof(SpriteRenderer))]
    public class ScrollingGrid : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float cellSize = 1f;

        private void LateUpdate()
        {
            if (target == null) return;

            float x = Mathf.Round(target.position.x / cellSize) * cellSize;
            float y = Mathf.Round(target.position.y / cellSize) * cellSize;
            transform.position = new Vector3(x, y, transform.position.z);
        }
    }
}
