using UnityEngine;

namespace ArcaneDrift
{
    // 우클릭 지점에 잠깐 나타났다가 작아지며 사라지는 표시
    [RequireComponent(typeof(SpriteRenderer))]
    public class ClickMarker : MonoBehaviour
    {
        // 원본 arcane-drift.html 의 값을 확인하지 못해 임시로 정한 수치
        [SerializeField] float life = 0.35f;
        [SerializeField] float startScale = 0.25f;

        SpriteRenderer sr;
        Color baseColor;
        float age;

        void Awake()
        {
            sr = GetComponent<SpriteRenderer>();
            baseColor = sr.color;
            transform.localScale = Vector3.one * startScale;
        }

        void Update()
        {
            age += Time.deltaTime;
            float k = age / life;
            if (k >= 1f) { Destroy(gameObject); return; }

            transform.localScale = Vector3.one * startScale * (1f - k);
            sr.color = new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * (1f - k));
        }
    }
}
