using UnityEngine;

namespace ArcaneDrift
{
    // 무한히 이어지는 것처럼 보이는 격자 배경.
    // 40×40 타일 판을 플레이어 근처로 옮기되, 칸 크기 단위로 맞춰(스냅) 옮기기 때문에 선은 제자리에 있는 것처럼 보인다.
    // 스프라이트를 따로 만들 필요 없이, 비어 있으면 격자 그림을 코드로 만든다.
    [RequireComponent(typeof(SpriteRenderer))]
    public class ScrollingGrid : MonoBehaviour
    {
        [SerializeField] Transform target;
        [SerializeField] Vector2 size = new Vector2(40, 40);
        // 원본 arcane-drift.html 의 격자 간격·선 색은 확인하지 못해 임시로 정한 수치
        [SerializeField] float cellSize = 1f;
        [SerializeField] Color background = new Color32(0x1A, 0x16, 0x26, 0xFF);
        [SerializeField] Color line = new Color32(0x2A, 0x24, 0x3D, 0xFF);

        void Awake()
        {
            var sr = GetComponent<SpriteRenderer>();
            if (sr.sprite == null)
            {
                sr.sprite = MakeCell();
                sr.color = Color.white;
            }
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.size = size;
            sr.sortingOrder = -10;
        }

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 p = target.position;
            float x = Mathf.Round(p.x / cellSize) * cellSize;
            float y = Mathf.Round(p.y / cellSize) * cellSize;
            transform.position = new Vector3(x, y, transform.position.z);
        }

        // 한 칸(cellSize 유닛)짜리 그림: 배경색 위에 왼쪽·아래쪽 테두리 선
        Sprite MakeCell()
        {
            const int px = 64;
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            var pixels = new Color[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    pixels[y * px + x] = (x < 2 || y < 2) ? line : background;
            tex.SetPixels(pixels);
            tex.Apply();
            // Tiled 그리기 모드는 Full Rect 메시가 필요하다
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px / cellSize, 0, SpriteMeshType.FullRect);
        }
    }
}
