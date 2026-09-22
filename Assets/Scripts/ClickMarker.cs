using UnityEngine;

namespace ArcaneDrift
{
    public class ClickMarker : MonoBehaviour
    {
        [SerializeField] private float lifetime = 0.4f;
        [SerializeField] private float startScale = 0.25f;

        private float timer;

        private void OnEnable()
        {
            timer = 0f;
            transform.localScale = Vector3.one * startScale;
        }

        private void Update()
        {
            timer += Time.deltaTime;
            float t = Mathf.Clamp01(timer / lifetime);
            transform.localScale = Vector3.one * startScale * (1f - t);

            if (t >= 1f)
            {
                Destroy(gameObject);
            }
        }
    }
}
