using UnityEngine;
using UnityEngine.InputSystem;

namespace InfiniteGarden
{
    /// <summary>
    /// Fly / infinite fall / tour camera that wraps around the repeating world.
    /// Every cell is identical, so jumping back by one tile size is invisible: that is the whole trick behind falling forever.
    /// Keys: G = fly (WASD, Q/E down/up, Shift faster, hold right mouse button to look), F = fall, T = tour.
    /// Fall and tour always start from the same place, so measurements are repeatable.
    /// </summary>
    public class IGFlyCamera : MonoBehaviour
    {
        public enum Mode { Fly, Fall, Tour }

        public IGWorldRepeater world;
        public Mode mode = Mode.Tour;

        [Header("Fly")]
        public float speed = 8f;
        public float fastMultiplier = 4f;
        public float lookSensitivity = 0.12f;

        [Header("Fall (keep this vertical shaft free of geometry)")]
        public Vector2 fallShaftXZ = Vector2.zero;
        public float maxFallSpeed = 45f;

        [Header("Tour (keep this straight corridor free of geometry)")]
        public Vector3 tourStart = new Vector3(-20f, 7f, -11f);
        public float tourYaw = 90f;
        public float tourSpeed = 5f;

        float m_Yaw, m_Pitch, m_FallSpeed, m_Time;

        void Start()
        {
            Vector3 euler = transform.eulerAngles;
            m_Yaw = euler.y;
            m_Pitch = euler.x > 180f ? euler.x - 360f : euler.x;

            if (mode == Mode.Tour) BeginTour();
            else if (mode == Mode.Fall) BeginFall();
        }

        /// <summary>Restarts the tour from its fixed start point (also used by IGBenchmark).</summary>
        public void BeginTour()
        {
            mode = Mode.Tour;
            transform.position = tourStart;
            m_Time = 0f;
        }

        void BeginFall()
        {
            mode = Mode.Fall;
            m_FallSpeed = 0f;
            transform.position = new Vector3(fallShaftXZ.x, transform.position.y, fallShaftXZ.y);
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.gKey.wasPressedThisFrame) mode = Mode.Fly;
                if (keyboard.fKey.wasPressedThisFrame) BeginFall();
                if (keyboard.tKey.wasPressedThisFrame) BeginTour();
            }

            float dt = Time.deltaTime;
            m_Time += dt;

            switch (mode)
            {
                case Mode.Fly: UpdateFly(keyboard, dt); break;
                case Mode.Fall: UpdateFall(dt); break;
                case Mode.Tour: UpdateTour(dt); break;
            }

            transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            Wrap();
        }

        void UpdateFly(Keyboard keyboard, float dt)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue() * lookSensitivity;
                m_Yaw += delta.x;
                m_Pitch = Mathf.Clamp(m_Pitch - delta.y, -89f, 89f);
            }

            if (keyboard == null)
                return;

            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move += Vector3.forward;
            if (keyboard.sKey.isPressed) move += Vector3.back;
            if (keyboard.aKey.isPressed) move += Vector3.left;
            if (keyboard.dKey.isPressed) move += Vector3.right;
            if (keyboard.eKey.isPressed) move += Vector3.up;
            if (keyboard.qKey.isPressed) move += Vector3.down;

            float currentSpeed = speed * (keyboard.leftShiftKey.isPressed ? fastMultiplier : 1f);
            transform.position += Quaternion.Euler(m_Pitch, m_Yaw, 0f) * move * (currentSpeed * dt);
        }

        void UpdateFall(float dt)
        {
            m_FallSpeed = Mathf.MoveTowards(m_FallSpeed, maxFallSpeed, 9.81f * dt);
            transform.position += Vector3.down * (m_FallSpeed * dt);
            m_Pitch = Mathf.MoveTowards(m_Pitch, 70f, 10f * dt); // slowly turn to look down into the abyss
            m_Yaw += 6f * dt;                                     // and spin a little
        }

        void UpdateTour(float dt)
        {
            // Move along a straight axis and look around gently.
            transform.position += Quaternion.Euler(0f, tourYaw, 0f) * Vector3.forward * (tourSpeed * dt);
            m_Yaw = tourYaw + Mathf.Sin(m_Time * 0.2f) * 25f;
            m_Pitch = Mathf.Sin(m_Time * 0.13f) * 12f;
        }

        void Wrap()
        {
            if (world == null)
                return;

            float size = world.tileSize;
            Vector3 origin = world.transform.position;
            Vector3 p = transform.position - origin;
            p.x = Mathf.Repeat(p.x + 0.5f * size, size) - 0.5f * size;
            p.y = Mathf.Repeat(p.y + 0.5f * size, size) - 0.5f * size;
            p.z = Mathf.Repeat(p.z + 0.5f * size, size) - 0.5f * size;
            transform.position = origin + p;
        }
    }
}
