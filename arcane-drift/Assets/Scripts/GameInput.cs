#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
#define USE_NEW_INPUT
#endif

using UnityEngine;
#if USE_NEW_INPUT
using UnityEngine.InputSystem;
#endif

namespace ArcaneDrift
{
    // 프로젝트의 입력 설정(Old / New Input System / Both)이 무엇이든 똑같이 동작하게 감싼 헬퍼.
    // 다른 스크립트는 Input.GetMouseButtonDown 대신 이것만 쓴다.
    public static class GameInput
    {
        public static bool RightClickDown()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        public static Vector2 MousePosition()
        {
#if USE_NEW_INPUT
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
    }
}
