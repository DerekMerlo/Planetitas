using UnityEngine;

namespace Planetitas
{
    /// <summary>
    /// Ajustes del juego editables desde Tools > Planetitas > Ajustes del juego.
    /// El juego los carga desde Resources/AjustesJuego y los lee en cada frame,
    /// así que los cambios hechos durante Play se ven al instante.
    /// </summary>
    public class JumpGameSettings : ScriptableObject
    {
        public const string ResourcePath = "AjustesJuego";

        [Header("Salto")]
        [Tooltip("Velocidad vertical del salto base (m/s).")]
        public float jumpSpeed = 9f;
        [Tooltip("Gravedad hacia abajo (m/s²).")]
        public float gravity = 18f;
        [Tooltip("Impulso extra que suma cada salto (0.12 = +12 %).")]
        public float impulseGrowthPerJump = 0.12f;
        [Tooltip("Multiplicador máximo de impulso.")]
        public float maxImpulseMultiplier = 2.5f;

        [Header("Estrellas de energía")]
        [Tooltip("Color de las estrellas que dan energía.")]
        public Color orbColor = new Color(0x8A / 255f, 0xA8 / 255f, 0xFF / 255f, 1f);
        [Tooltip("Color del pequeño destello que parpadea alrededor de cada estrella.")]
        public Color sparkleColor = new Color(0x7F / 255f, 0xF3 / 255f, 0xFF / 255f, 1f);
        [Tooltip("Intensidad del brillo (emisión).")]
        public float orbGlow = 2.5f;
        [Tooltip("Distancia vertical entre estrellas al empezar (m).")]
        public float startOrbSpacing = 2.5f;
        [Tooltip("Distancia vertical máxima entre estrellas, a la que se llega subiendo (m).")]
        public float maxOrbSpacing = 7f;

        public void ResetToDefaults()
        {
            var defaults = CreateInstance<JumpGameSettings>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(defaults), this);
            DestroyImmediate(defaults);
        }

        /// <summary>Altura que alcanza un salto con el multiplicador dado: v² / 2g.</summary>
        public float JumpHeight(float multiplier)
        {
            float v = jumpSpeed * multiplier;
            return v * v / (2f * Mathf.Max(0.01f, gravity));
        }

        public float FirstJumpHeight => JumpHeight(Mathf.Min(maxImpulseMultiplier, 1f + impulseGrowthPerJump));
        public float MaxJumpHeight => JumpHeight(maxImpulseMultiplier);

        public static JumpGameSettings LoadOrDefault()
        {
            var s = Resources.Load<JumpGameSettings>(ResourcePath);
            return s != null ? s : CreateInstance<JumpGameSettings>();
        }
    }
}
