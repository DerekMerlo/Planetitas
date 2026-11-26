using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Planetitas
{
    /// <summary>Única fuente de color del juego (art bible).</summary>
    public static class Palette
    {
        public static readonly Color Sky = Hex(0x1B1335);
        public static readonly Color Player = Hex(0xFF7A59);
        public static readonly Color Ring = Hex(0xFFD166);
        public static readonly Color Energy = Hex(0x5CE1E6);
        public static readonly Color Ground = Hex(0x7B5CFF);
        public static readonly Color Danger = Hex(0xFF4D6D);
        public static readonly Color Star = Hex(0xF4F1FF);  // estrellitas del fondo (decorativas)
        public static readonly Color Panel = new Color(0.04f, 0.03f, 0.10f, 0.65f);

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);
    }

    /// <summary>
    /// Juego de saltos: cada salto consume energía pero da más impulso que el anterior.
    /// Recoge estrellas de energía para recargar energía. Si la energía llega a cero, pierdes.
    /// </summary>
    public class EnergyJumpGame : MonoBehaviour
    {
        [Header("Energía")]
        public float maxEnergy = 100f;
        public float jumpCost = 10f;
        public float baseDrainPerSecond = 3f;
        public float maxDrainPerSecond = 9f;
        public float orbEnergy = 25f;

        [Header("Movimiento")]
        public float impulseDecayPerSecond = 0.12f;  // el impulso extra se pierde si dejas de saltar
        public float horizontalSpeed = 7f;
        public float arenaHalfWidth = 5.5f;

        // Salto, gravedad y esferas: se editan en Tools > Planetitas > Ajustes del juego.
        JumpGameSettings settings;
        Color appliedOrbColor;
        float appliedOrbGlow = -1f;
        Color appliedSparkleColor;

        enum State { Ready, Playing, GameOver }

        const float PlayerRadius = 0.5f;
        const float OrbPickupDistance = 1.0f;
        const float CameraDistance = 14f;
        const string BestKey = "Planetitas_BestHeight";

        State state;
        Camera cam;
        Transform player, playerVisual, ground;
        TrailRenderer trail;
        Vector2 velocity;
        float energy, displayedEnergy, multiplier = 1f, height, bestHeight, cameraY, nextOrbY, lowEnergyBlink;
        int jumps;
        bool grounded;
        string gameOverReason = "";
        readonly List<Transform> orbs = new List<Transform>();          // estrellas de energía (recogibles)
        readonly List<ParticleSystem> sparkles = new List<ParticleSystem>(); // destello de cada estrella, mismo índice que orbs
        readonly List<Transform> stars = new List<Transform>();
        Shader litShader;
        Material playerMat, ringMat, orbMat, groundMat, starMat, trailMat, sparkleMat;
        Mesh starMesh;
        AudioSource sfx, ambient;
        AudioClip jumpClip, orbClip, loseClip, clickClip;
        Texture2D white;
        GUIStyle bigStyle, midStyle, smallStyle;
        Coroutine popRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != "SampleScene" && scene != "") return; // solo la escena del juego (o una sin guardar)
            if (FindAnyObjectByType<EnergyJumpGame>() != null) return;
            new GameObject("EnergyJumpGame").AddComponent<EnergyJumpGame>();
        }

        void Awake()
        {
            bestHeight = PlayerPrefs.GetFloat(BestKey, 0f);
            settings = JumpGameSettings.LoadOrDefault();
            litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null) litShader = Shader.Find("Standard");

            playerMat = MakeMat(Palette.Player, 0.25f);
            ringMat = MakeMat(Palette.Ring, 0.6f);
            orbMat = MakeMat(settings.orbColor, settings.orbGlow);
            orbMat.SetFloat("_Smoothness", 0.8f); // acabado brillante, como la referencia de XP-27
            groundMat = MakeMat(Palette.Ground, 0.4f);
            starMat = MakeMat(Palette.Star, 0.6f); // fondo tenue para no confundirse con las estrellas de energía
            trailMat = new Material(Shader.Find("Sprites/Default"));
            sparkleMat = new Material(trailMat) { mainTexture = SparkleTexture() };
            starMesh = BuildStarMesh();

            white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white);
            white.Apply();

            SetupCamera();
            SetupAudio();
            BuildWorld();
            ResetRun();
        }

        // ---------- montaje ----------

        Material MakeMat(Color c, float emission)
        {
            var m = new Material(litShader);
            m.SetColor("_BaseColor", c);
            m.color = c;
            m.SetFloat("_Smoothness", 0.35f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * emission);
            }
            return m;
        }

        GameObject Primitive(PrimitiveType type, Material mat, Transform parent, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>()); // colisiones calculadas a mano
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        void SetupCamera()
        {
            cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Palette.Sky;
            cam.fieldOfView = 50f;
            cam.transform.rotation = Quaternion.identity;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.Lerp(Palette.Sky, Palette.Star, 0.35f);
        }

        void BuildWorld()
        {
            ground = Primitive(PrimitiveType.Cube, groundMat, null, "Plataforma").transform;
            ground.localScale = new Vector3(arenaHalfWidth * 2f + 1f, 1f, 3f);
            ground.position = new Vector3(0f, -0.5f, 0f);

            player = new GameObject("Planetita").transform;
            playerVisual = Primitive(PrimitiveType.Sphere, playerMat, player, "Cuerpo").transform;
            var ring = Primitive(PrimitiveType.Cylinder, ringMat, playerVisual, "Anillo").transform;
            ring.localScale = new Vector3(1.8f, 0.015f, 1.8f);
            ring.localRotation = Quaternion.Euler(18f, 0f, -22f);

            trail = player.gameObject.AddComponent<TrailRenderer>();
            trail.sharedMaterial = trailMat;
            trail.time = 0.35f;
            trail.startWidth = 0.6f;
            trail.endWidth = 0f;
            trail.startColor = new Color(Palette.Ring.r, Palette.Ring.g, Palette.Ring.b, 0.7f);
            trail.endColor = new Color(Palette.Player.r, Palette.Player.g, Palette.Player.b, 0f);
        }

        void SetupAudio()
        {
            sfx = gameObject.AddComponent<AudioSource>();
            sfx.playOnAwake = false;
            sfx.spatialBlend = 0f;

            jumpClip = Synth.Sweep("salto", 330f, 660f, 0.12f, 0.35f);
            orbClip = Synth.Sweep("orbe", 880f, 1320f, 0.09f, 0.35f);
            loseClip = Synth.Sting("perder", new[] { 523f, 415f, 330f }, 0.16f, 0.35f);
            clickClip = Synth.Noise("click", 0.02f, 0.25f);

            ambient = gameObject.AddComponent<AudioSource>();
            ambient.clip = Synth.Ambient("ambiente", 6f, 0.12f);
            ambient.loop = true;
            ambient.volume = 0.5f;
            ambient.Play();
        }

        // ---------- partida ----------

        void ResetRun()
        {
            foreach (var o in orbs) Destroy(o.gameObject);
            orbs.Clear();
            sparkles.Clear();
            foreach (var s in stars) Destroy(s.gameObject);
            stars.Clear();

            state = State.Ready;
            energy = displayedEnergy = maxEnergy;
            multiplier = 1f;
            jumps = 0;
            height = 0f;
            velocity = Vector2.zero;
            grounded = true;
            player.position = new Vector3(0f, PlayerRadius, 0f);
            playerVisual.localScale = Vector3.one;
            trail.Clear();
            cameraY = 0f;
            nextOrbY = settings.startOrbSpacing * 0.88f; // primera estrella algo más cerca
            SpawnAhead();
            PlaceCamera(true);
        }

        void Update()
        {
            bool jumpPressed = JumpPressed();
            float dt = Time.deltaTime;

            switch (state)
            {
                case State.Ready:
                    IdleBob();
                    if (jumpPressed) { state = State.Playing; Jump(); }
                    break;

                case State.Playing:
                    Simulate(dt, jumpPressed);
                    break;

                case State.GameOver:
                    bool restart = jumpPressed || (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame);
                    if (restart) { sfx.PlayOneShot(clickClip); ResetRun(); }
                    break;
            }

            ApplyOrbLook();
            AnimateOrbs();
            displayedEnergy = Mathf.Lerp(displayedEnergy, energy, 1f - Mathf.Exp(-12f * dt)); // la barra se anima, no salta
            lowEnergyBlink += dt;
            PlaceCamera(false);
        }

        void Simulate(float dt, bool jumpPressed)
        {
            // energía: se gasta con el tiempo (más rápido cuanto más alto)
            float drain = Mathf.Min(maxDrainPerSecond, baseDrainPerSecond + height * 0.02f);
            energy -= drain * dt;

            if (jumpPressed && energy >= jumpCost) Jump();

            // el impulso extra decae poco a poco si no sigues saltando
            multiplier = Mathf.Max(1f, multiplier - impulseDecayPerSecond * dt);

            // movimiento
            velocity.y -= settings.gravity * dt;
            float targetX = HorizontalInput() * horizontalSpeed;
            velocity.x = Mathf.MoveTowards(velocity.x, targetX, horizontalSpeed * 6f * dt);
            Vector3 p = player.position + new Vector3(velocity.x, velocity.y, 0f) * dt;
            p.x = Mathf.Clamp(p.x, -arenaHalfWidth, arenaHalfWidth);

            // la plataforma inicial
            if (p.y <= PlayerRadius && velocity.y <= 0f)
            {
                p.y = PlayerRadius;
                if (!grounded) Squash();
                velocity.y = 0f;
                grounded = true;
                multiplier = 1f;
                jumps = 0;
            }
            player.position = p;
            player.rotation = Quaternion.Euler(0f, 0f, -velocity.x * 3f); // se inclina hacia donde se mueve
            playerVisual.Rotate(0f, 90f * dt, 0f, Space.Self);

            height = Mathf.Max(height, p.y - PlayerRadius);

            // estrellas de energía
            for (int i = orbs.Count - 1; i >= 0; i--)
            {
                var o = orbs[i];
                if (Vector2.Distance(o.position, p) < OrbPickupDistance) CollectOrb(i);
                else if (o.position.y < cameraY - 12f) RemoveOrb(i);
            }
            SpawnAhead();

            // derrota
            if (energy <= 0f) { energy = 0f; Lose("¡Te quedaste sin energía!"); }
            else if (p.y < cameraY - ViewHalfHeight() + 1f && !grounded) Lose("¡Caíste al vacío!");
        }

        void Jump()
        {
            energy -= jumpCost;
            jumps++;
            grounded = false;
            multiplier = Mathf.Min(settings.maxImpulseMultiplier, multiplier + settings.impulseGrowthPerJump);
            velocity.y = settings.jumpSpeed * multiplier;
            sfx.pitch = 0.9f + (multiplier - 1f) * 0.4f; // el sonido sube con el impulso
            sfx.PlayOneShot(jumpClip);
            Pop(new Vector3(0.8f, 1.25f, 0.8f), 0.18f);
        }

        void CollectOrb(int index)
        {
            var o = orbs[index];
            energy = Mathf.Min(maxEnergy, energy + orbEnergy);
            sfx.pitch = Random.Range(0.92f, 1.08f);
            sfx.PlayOneShot(orbClip);
            Burst(o.position, settings.orbColor, 10);
            Burst(o.position, settings.sparkleColor, 8, sparkleMat);
            FloatingText(o.position, "+" + Mathf.RoundToInt(orbEnergy));
            Pop(new Vector3(1.25f, 1.25f, 1.25f), 0.15f);
            RemoveOrb(index);
        }

        void RemoveOrb(int index)
        {
            Destroy(orbs[index].gameObject);
            orbs.RemoveAt(index);
            sparkles.RemoveAt(index);
        }

        void Lose(string reason)
        {
            state = State.GameOver;
            gameOverReason = reason;
            sfx.pitch = 1f;
            sfx.PlayOneShot(loseClip);
            Burst(player.position, Palette.Danger, 24);
            StartCoroutine(Shake(0.35f, 0.35f));
            if (height > bestHeight)
            {
                bestHeight = height;
                PlayerPrefs.SetFloat(BestKey, bestHeight);
                PlayerPrefs.Save();
            }
        }

        // ---------- mundo ----------

        void SpawnAhead()
        {
            while (nextOrbY < cameraY + 18f)
            {
                var orb = MakeEnergyStar();
                orb.position = new Vector3(Random.Range(-arenaHalfWidth + 0.5f, arenaHalfWidth - 0.5f), nextOrbY, 0f);
                orbs.Add(orb);
                sparkles.Add(MakeSparkle(orb));
                // las estrellas se separan más cuanto más alto llegas
                nextOrbY += Mathf.Min(Mathf.Max(settings.startOrbSpacing, settings.maxOrbSpacing), settings.startOrbSpacing + nextOrbY * 0.02f) * Random.Range(0.85f, 1.15f);

                for (int i = 0; i < 3; i++)
                {
                    var s = Primitive(PrimitiveType.Sphere, starMat, null, "EstrellaFondo").transform;
                    s.localScale = Vector3.one * Random.Range(0.04f, 0.1f);
                    s.position = new Vector3(Random.Range(-14f, 14f), nextOrbY + Random.Range(-3f, 3f), Random.Range(6f, 14f));
                    stars.Add(s);
                }
            }
            for (int i = stars.Count - 1; i >= 0; i--)
                if (stars[i].position.y < cameraY - 20f) { Destroy(stars[i].gameObject); stars.RemoveAt(i); }
        }

        // ---------- estrellas de energía (XP-27) ----------

        const float StarScale = 0.8f;      // tamaño base de la estrella
        const float SquashHz = 3.5f;       // squash & stretch rápido: llama la atención
        const float SquashAmount = 0.2f;

        Transform MakeEnergyStar()
        {
            var go = new GameObject("EstrellaEnergia");
            go.AddComponent<MeshFilter>().sharedMesh = starMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = orbMat;
            go.transform.localScale = Vector3.one * StarScale;
            return go.transform;
        }

        ParticleSystem MakeSparkle(Transform star)
        {
            // pequeño destello celeste que parpadea alrededor de la estrella
            var go = new GameObject("Destello");
            go.transform.SetParent(star, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.3f); // delante de la estrella
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.duration = 1f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.6f);
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 0.25f);
            main.startColor = settings.sparkleColor;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Local; // el squash de la estrella no deforma el destello
            main.maxParticles = 6;
            var emission = ps.emission;
            emission.rateOverTime = 3.5f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.45f;
            shape.radiusThickness = 0.3f; // sobre el borde de la estrella, no en el centro
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0f)));
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = sparkleMat;
            ps.Simulate(Random.Range(0f, 1f)); // cada estrella empieza en un momento distinto
            ps.Play();
            return ps;
        }

        void ApplyOrbLook()
        {
            // aplica en vivo los colores y el brillo elegidos en la ventana de ajustes
            if (settings.sparkleColor != appliedSparkleColor)
            {
                appliedSparkleColor = settings.sparkleColor;
                foreach (var ps in sparkles)
                {
                    var main = ps.main;
                    main.startColor = appliedSparkleColor;
                }
            }
            if (settings.orbColor == appliedOrbColor && Mathf.Approximately(settings.orbGlow, appliedOrbGlow)) return;
            appliedOrbColor = settings.orbColor;
            appliedOrbGlow = settings.orbGlow;
            orbMat.SetColor("_BaseColor", appliedOrbColor);
            orbMat.color = appliedOrbColor;
            orbMat.EnableKeyword("_EMISSION");
            orbMat.SetColor("_EmissionColor", appliedOrbColor * appliedOrbGlow);
        }

        void AnimateOrbs()
        {
            float t = Time.time;
            foreach (var o in orbs)
            {
                float phase = o.position.x * 1.7f + o.position.y; // cada estrella con su propio ritmo
                float s = Mathf.Sin(t * SquashHz * 2f * Mathf.PI + phase);
                s = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 0.6f); // más "snappy" que un seno
                float stretch = 1f + s * SquashAmount;            // se estira hacia arriba...
                float squash = 1f / Mathf.Sqrt(stretch);           // ...y adelgaza (conserva volumen)
                o.localScale = new Vector3(squash, stretch, squash) * StarScale;
                o.localRotation = Quaternion.Euler(0f, Mathf.Sin(t * 1.3f + phase) * 25f, Mathf.Sin(t * 0.9f + phase) * 8f);
            }
        }

        void IdleBob()
        {
            float s = 1f + Mathf.Sin(Time.time * Mathf.PI) * 0.03f;
            playerVisual.localScale = new Vector3(1f / s, s, 1f / s);
        }

        float ViewHalfHeight() => Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * CameraDistance;

        void PlaceCamera(bool snap)
        {
            // la cámara solo sube: sigue a la planetita con suavizado
            float target = Mathf.Max(cameraY, player.position.y - 1f);
            cameraY = snap ? target : Mathf.Lerp(cameraY, target, 1f - Mathf.Exp(-5f * Time.deltaTime));
            cam.transform.position = new Vector3(0f, cameraY + 3f, -CameraDistance);
        }

        // ---------- sensación (juice) ----------

        void Pop(Vector3 peak, float duration)
        {
            if (popRoutine != null) StopCoroutine(popRoutine);
            popRoutine = StartCoroutine(PopRoutine(peak, duration));
        }

        IEnumerator PopRoutine(Vector3 peak, float duration)
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / duration)
            {
                playerVisual.localScale = Vector3.LerpUnclamped(peak, Vector3.one, OutBack(t));
                yield return null;
            }
            playerVisual.localScale = Vector3.one;
        }

        void Squash() => Pop(new Vector3(1.2f, 0.8f, 1.2f), 0.12f);

        static float OutBack(float t)
        {
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        IEnumerator Shake(float duration, float strength)
        {
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                float k = strength * (1f - t / duration);
                cam.transform.position += (Vector3)Random.insideUnitCircle * k;
                yield return null;
            }
        }

        void Burst(Vector3 at, Color color, int count, Material material = null)
        {
            var go = new GameObject("Destello");
            go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = 0.5f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.1f, 0.25f);
            main.startColor = color;
            main.gravityModifier = 0.5f;
            main.stopAction = ParticleSystemStopAction.Destroy;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.2f;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = material != null ? material : trailMat;
            ps.Play();
        }

        // ---------- geometría procedural ----------

        /// <summary>
        /// Estrella de 5 puntas "inflada", con las puntas redondeadas (referencia de XP-27).
        /// La altura se obtiene "inflando" el contorno: se resuelve ∇²h = -1 con h = 0 en el borde
        /// (como hinchar un globo con esa forma) y se usa z = √h para que el borde sea redondo.
        /// </summary>
        static Mesh BuildStarMesh()
        {
            const int segments = 100, rings = 12, iterations = 800;
            const float outer = 0.5f, inner = 0.3f, depth = 0.22f;
            const double omega = 1.95; // sobre-relajación: converge en pocas iteraciones

            // contorno: radio según el ángulo, con una punta hacia arriba
            var outline = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float a = i * 2f * Mathf.PI / segments;
                float tip = Mathf.Pow(0.5f + 0.5f * Mathf.Cos(5f * a), 1.2f);
                float r = Mathf.Lerp(inner, outer, tip);
                float ang = a + Mathf.PI * 0.5f;
                outline[i] = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
            }
            Vector2 Pos(int k, int i) => outline[i] * ((float)k / rings);

            // pesos 1/d² hacia los 4 vecinos de cada punto de la malla polar (k = anillo, i = ángulo)
            var h = new double[rings + 1, segments];
            var w = new double[rings, segments, 4];
            for (int k = 1; k < rings; k++)
                for (int i = 0; i < segments; i++)
                {
                    Vector2 p = Pos(k, i);
                    w[k, i, 0] = 1.0 / (Pos(k - 1, i) - p).sqrMagnitude;
                    w[k, i, 1] = 1.0 / (Pos(k + 1, i) - p).sqrMagnitude;
                    w[k, i, 2] = 1.0 / (Pos(k, (i + 1) % segments) - p).sqrMagnitude;
                    w[k, i, 3] = 1.0 / (Pos(k, (i + segments - 1) % segments) - p).sqrMagnitude;
                }
            double centerWeight = 0;
            for (int i = 0; i < segments; i++) centerWeight += 1.0 / Pos(1, i).sqrMagnitude;

            double center = 0;
            for (int it = 0; it < iterations; it++)
            {
                double sum = 0;
                for (int i = 0; i < segments; i++) sum += h[1, i] / Pos(1, i).sqrMagnitude;
                center += omega * ((sum + segments) / centerWeight - center);
                for (int i = 0; i < segments; i++) h[0, i] = center;

                for (int k = 1; k < rings; k++)
                    for (int i = 0; i < segments; i++)
                    {
                        int ip = (i + 1) % segments, im = (i + segments - 1) % segments;
                        double w0 = w[k, i, 0], w1 = w[k, i, 1], w2 = w[k, i, 2], w3 = w[k, i, 3];
                        double target = (w0 * h[k - 1, i] + w1 * h[k + 1, i] + w2 * h[k, ip] + w3 * h[k, im] + 4.0) / (w0 + w1 + w2 + w3);
                        h[k, i] += omega * (target - h[k, i]);
                    }
            }

            // dos caras (frente z<0 hacia la cámara, dorso z>0) que comparten el anillo del borde
            var verts = new List<Vector3>();
            var tris = new List<int>();
            int[] Face(float side)
            {
                var idx = new int[rings + 1];
                idx[0] = verts.Count;
                verts.Add(new Vector3(0f, 0f, side * depth));
                for (int k = 1; k < rings; k++)
                {
                    idx[k] = verts.Count;
                    for (int i = 0; i < segments; i++)
                    {
                        float z = side * depth * Mathf.Sqrt(Mathf.Max(0f, (float)(h[k, i] / center)));
                        Vector2 p = Pos(k, i);
                        verts.Add(new Vector3(p.x, p.y, z));
                    }
                }
                return idx;
            }
            int[] front = Face(-1f), back = Face(1f);
            int edge = verts.Count;
            for (int i = 0; i < segments; i++) verts.Add(outline[i]);
            front[rings] = back[rings] = edge;

            void Build(int[] f, bool flip)
            {
                for (int i = 0; i < segments; i++)
                {
                    int j = (i + 1) % segments;
                    Tri(f[0], f[1] + j, f[1] + i, flip); // abanico central
                    for (int k = 1; k < rings; k++)
                    {
                        int a0 = f[k] + i, a1 = f[k] + j, b0 = f[k + 1] + i, b1 = f[k + 1] + j;
                        Tri(a0, a1, b0, flip);
                        Tri(a1, b1, b0, flip);
                    }
                }
            }
            void Tri(int a, int b, int c, bool flip)
            {
                tris.Add(a);
                if (flip) { tris.Add(c); tris.Add(b); } else { tris.Add(b); tris.Add(c); }
            }
            Build(front, false);
            Build(back, true);

            var mesh = new Mesh { name = "EstrellaEnergia" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Textura de destello de 4 puntas, blanca con alfa (se tiñe con el color del destello).</summary>
        static Texture2D SparkleTexture()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = Mathf.Abs((x + 0.5f) / n * 2f - 1f), v = Mathf.Abs((y + 0.5f) / n * 2f - 1f);
                    float rays = Mathf.Max(Mathf.Exp(-u * 14f) * (1f - v), Mathf.Exp(-v * 14f) * (1f - u));
                    float core = Mathf.Exp(-(u * u + v * v) * 18f);
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(rays + core));
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        void FloatingText(Vector3 at, string text)
        {
            var go = new GameObject("Texto");
            go.transform.position = at;
            var tm = go.AddComponent<TextMesh>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            tm.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.fontSize = 64;
            tm.characterSize = 0.06f;
            tm.color = settings.orbColor;
            StartCoroutine(FloatRoutine(tm));
        }

        IEnumerator FloatRoutine(TextMesh tm)
        {
            Vector3 start = tm.transform.position;
            for (float t = 0f; t < 1f; t += Time.deltaTime / 0.6f)
            {
                tm.transform.position = start + Vector3.up * 0.8f * t;
                var c = settings.orbColor; c.a = 1f - t;
                tm.color = c;
                yield return null;
            }
            Destroy(tm.gameObject);
        }

        // ---------- entrada ----------

        static bool JumpPressed()
        {
            var k = Keyboard.current;
            if (k != null && (k.spaceKey.wasPressedThisFrame || k.wKey.wasPressedThisFrame || k.upArrowKey.wasPressedThisFrame)) return true;
            var m = Mouse.current;
            if (m != null && m.leftButton.wasPressedThisFrame) return true;
            var t = Touchscreen.current;
            if (t != null && t.primaryTouch.press.wasPressedThisFrame) return true;
            var g = Gamepad.current;
            return g != null && g.buttonSouth.wasPressedThisFrame;
        }

        static float HorizontalInput()
        {
            float x = 0f;
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.aKey.isPressed || k.leftArrowKey.isPressed) x -= 1f;
                if (k.dKey.isPressed || k.rightArrowKey.isPressed) x += 1f;
            }
            var g = Gamepad.current;
            if (g != null && Mathf.Abs(g.leftStick.x.ReadValue()) > 0.2f) x = g.leftStick.x.ReadValue();
            var t = Touchscreen.current;
            if (t != null && t.primaryTouch.press.isPressed)
                x = t.primaryTouch.position.ReadValue().x < Screen.width * 0.5f ? -1f : 1f;
            var m = Mouse.current;
            if (x == 0f && m != null && m.leftButton.isPressed)
                x = Mathf.Clamp((m.position.ReadValue().x / Screen.width - 0.5f) * 4f, -1f, 1f);
            return Mathf.Clamp(x, -1f, 1f);
        }

        // ---------- interfaz ----------

        void OnGUI()
        {
            if (bigStyle == null)
            {
                bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                midStyle = new GUIStyle(bigStyle) { fontSize = 32, fontStyle = FontStyle.Normal };
                smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            }
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale;

            // barra de energía
            Rect bar = new Rect(40f, 40f, 520f, 40f);
            Fill(new Rect(bar.x - 6f, bar.y - 6f, bar.width + 12f, bar.height + 12f), Palette.Panel);
            float frac = Mathf.Clamp01(displayedEnergy / maxEnergy);
            bool low = frac < 0.25f && state == State.Playing;
            Color barColor = low && Mathf.Repeat(lowEnergyBlink, 0.4f) < 0.2f ? Palette.Danger : Palette.Energy;
            Fill(new Rect(bar.x, bar.y, bar.width * frac, bar.height), barColor);
            Label(new Rect(bar.x + 12f, bar.y, bar.width, bar.height), "ENERGÍA  " + Mathf.CeilToInt(energy), smallStyle, Palette.Sky);

            // impulso y altura
            Label(new Rect(40f, 96f, 600f, 40f), $"IMPULSO  x{multiplier:0.00}", smallStyle, Palette.Ring);
            var right = new GUIStyle(smallStyle) { alignment = TextAnchor.UpperRight };
            Label(new Rect(w - 640f, 40f, 600f, 40f), $"ALTURA  {height:0} m", right, Palette.Star);
            Label(new Rect(w - 640f, 80f, 600f, 40f), $"RÉCORD  {bestHeight:0} m", right, Palette.Ring);

            float h = 1080f;
            if (state == State.Ready)
            {
                Fill(new Rect(0f, h * 0.30f, w, 300f), Palette.Panel);
                Label(new Rect(0f, h * 0.30f + 30f, w, 90f), "PLANETITAS", bigStyle, Palette.Ring);
                Label(new Rect(0f, h * 0.30f + 130f, w, 50f), "Salta para alcanzar la energía. Cada salto te impulsa más alto.", midStyle, Palette.Star);
                Label(new Rect(0f, h * 0.30f + 190f, w, 50f), "ESPACIO / clic: saltar    A-D / flechas: moverte", midStyle, Palette.Energy);
            }
            else if (state == State.GameOver)
            {
                Fill(new Rect(0f, h * 0.30f, w, 300f), Palette.Panel);
                Label(new Rect(0f, h * 0.30f + 30f, w, 90f), gameOverReason, bigStyle, Palette.Danger);
                Label(new Rect(0f, h * 0.30f + 130f, w, 50f), $"Llegaste a {height:0} m   ·   Récord: {bestHeight:0} m", midStyle, Palette.Star);
                Label(new Rect(0f, h * 0.30f + 190f, w, 50f), "ESPACIO / clic / R para reintentar", midStyle, Palette.Energy);
            }
        }

        void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, white);
            GUI.color = old;
        }

        static void Label(Rect r, string text, GUIStyle style, Color c)
        {
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }
    }

    /// <summary>Sonidos sintetizados en código (sin archivos de audio).</summary>
    static class Synth
    {
        const int Rate = 44100;

        static AudioClip Make(string name, float[] data)
        {
            int fade = Mathf.Min(data.Length / 2, Rate / 200); // 5 ms de fundido para evitar clics
            for (int i = 0; i < fade; i++)
            {
                float k = (float)i / fade;
                data[i] *= k;
                data[data.Length - 1 - i] *= k;
            }
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        public static AudioClip Sweep(string name, float from, float to, float seconds, float volume)
        {
            int n = (int)(Rate * seconds);
            var d = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                phase += 2f * Mathf.PI * Mathf.Lerp(from, to, t) / Rate;
                d[i] = Mathf.Sin(phase) * (1f - t) * volume;
            }
            return Make(name, d);
        }

        public static AudioClip Sting(string name, float[] notes, float noteSeconds, float volume)
        {
            int per = (int)(Rate * noteSeconds);
            var d = new float[per * notes.Length + per];
            for (int n = 0; n < notes.Length; n++)
                for (int i = 0; i < per * 2 && n * per + i < d.Length; i++)
                {
                    float t = (float)i / (per * 2);
                    d[n * per + i] += Mathf.Sin(2f * Mathf.PI * notes[n] * i / Rate) * (1f - t) * volume * 0.6f;
                }
            return Make(name, d);
        }

        public static AudioClip Noise(string name, float seconds, float volume)
        {
            int n = (int)(Rate * seconds);
            var d = new float[n];
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                lp = Mathf.Lerp(lp, Random.Range(-1f, 1f), 0.3f);
                d[i] = lp * (1f - (float)i / n) * volume;
            }
            return Make(name, d);
        }

        public static AudioClip Ambient(string name, float seconds, float volume)
        {
            int n = (int)(Rate * seconds);
            var d = new float[n];
            float brown = 0f;
            for (int i = 0; i < n; i++)
            {
                brown = Mathf.Clamp(brown + Random.Range(-0.02f, 0.02f), -1f, 1f);
                float swell = Mathf.Sin(2f * Mathf.PI * 55f * i / Rate) * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * i / n));
                d[i] = (brown * 0.6f + swell * 0.4f) * volume;
            }
            return Make(name, d);
        }
    }
}



