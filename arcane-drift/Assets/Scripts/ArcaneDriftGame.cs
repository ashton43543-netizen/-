#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
#define USE_NEW_INPUT
#endif

using System.Collections.Generic;
using UnityEngine;
#if USE_NEW_INPUT
using UnityEngine.InputSystem;
#endif

// Arcane Drift — 무한 모드 (STEP 1~8 전체)
//
// 이 파일 하나를 Assets 아래 아무 곳에 넣고 ▶ 를 누르면 된다. 씬에 아무것도 배치할 필요가 없다.
//
// 구조
//  - MonoBehaviour 는 ArcaneDriftGame 하나뿐이다. 적·투사체·경험치 구슬·효과는 일반 클래스로 두고
//    ArcaneDriftGame.Update 에서 직접 움직인다. (Unity는 MonoBehaviour 마다 같은 이름의 파일을 요구하기 때문)
//  - 물리(Rigidbody2D)는 쓰지 않는다. 위치를 직접 옮기고 충돌은 거리로 판정한다.
//  - 적·투사체·구슬·효과는 풀링한다. 죽은 것은 alive = false 로 표시해 두고 프레임 끝에 한꺼번에 풀로 돌려보낸다.
//  - 수치는 맨 아래 Tune 에 모았다. "[임시]" 는 원본 arcane-drift.html 을 확인하지 못해 정한 값이다.
namespace ArcaneDrift
{
    public class ArcaneDriftGame : MonoBehaviour
    {
        enum State { Title, Playing, LevelUp, Paused, GameOver }

        const string BestTimeKey = "ArcaneDrift.BestTime";
        const string BestKillsKey = "ArcaneDrift.BestKills";
        const float FloatTextLife = 0.7f;

        static ArcaneDriftGame instance;

        // 씬이 열리면 자동으로 게임 오브젝트를 만든다
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (instance == null) new GameObject("ArcaneDrift").AddComponent<ArcaneDriftGame>();
        }

        State state = State.Title;
        Camera cam;
        Transform grid;

        // ---- 플레이어 ----
        Transform playerTf, playerBody, targetRing;
        SpriteRenderer playerSr;
        bool walking;
        float walkTime;
        Vector2 ppos, facing = Vector2.right;
        Vector2? moveTarget;
        bool holdMove;
        Enemy attackTarget;
        int attackSerial;
        float hp, attackCd, attackLock, hurtFlash, eBuff;
        int attackCount;
        PlayerMods mods;
        SkillState[] skills;
        readonly Dictionary<string, int> stacks = new Dictionary<string, int>();
        bool dashing, dashKilled;

        // ---- 진행 ----
        float elapsed, spawnTimer, xp, bannerTime;
        int eliteIndex, bossIndex, kills, level, xpNeed, pendingLevelUps, serial;
        string bannerText = "";
        readonly List<Card> cards = new List<Card>();
        float bestTime;
        int bestKills;
        bool newRecord;

        // ---- 풀 ----
        Pool<Enemy> enemies;
        Pool<Projectile> projectiles;
        Pool<XpOrb> orbs;
        Pool<Fx> fxs;
        readonly List<FloatText> texts = new List<FloatText>();
        readonly List<Enemy> nearby = new List<Enemy>();
        readonly HashSet<Enemy> chainSet = new HashSet<Enemy>();
        readonly HashSet<Enemy> dashSet = new HashSet<Enemy>();

        // ---- 계산된 능력치 ----
        float MaxHp { get { return Tune.PlayerHp + mods.maxHpAdd; } }
        float Attack { get { return (Tune.PlayerDamage + mods.damageAdd) * (1 + mods.damagePct); } }
        float Aspd { get { return Tune.PlayerAspd * (1 + mods.aspdPct) + (eBuff > 0 ? Tune.EBuffAspd : 0); } }
        float Range { get { return Tune.PlayerRange + mods.rangeAdd; } }
        float MoveSpeed
        {
            get
            {
                float baseSpeed = Tune.PlayerSpeedMatchesMonsters ? EnemyKind.Grunt.speed * Tune.SpeedScale(elapsed) : Tune.PlayerSpeed;
                return baseSpeed * (1 + mods.movePct);
            }
        }
        float CritChance { get { return Tune.CritChance + mods.critChance; } }
        float CritMult { get { return Tune.CritMult + mods.critMult; } }
        bool AllSkillsLearned { get { return skills[0].level > 0 && skills[1].level > 0 && skills[2].level > 0; } }

        // ================================================================ 시작

        void Awake()
        {
            if (instance != null && instance != this) { Destroy(gameObject); return; }
            instance = this;
            useGUILayout = false;

            Art.Init();
            cam = SetupCamera();
            grid = Art.CreateGrid(transform, 1f, 40f, Palette.Ground, Palette.GridLine);

            // 플레이어: 그림자 + 도트 캐릭터. 맞는 판정은 그림과 상관없이 반지름 PlayerRadius 원이다
            playerTf = new GameObject("Player").transform;
            playerTf.SetParent(transform, false);
            var shadow = Art.CreateCircle("Shadow", playerTf, new Color(0f, 0f, 0f, 0.35f), 1f, 7);
            shadow.localScale = new Vector3(0.38f, 0.13f, 1f);
            shadow.localPosition = new Vector3(0f, -Art.HeroFeet, 0f);
            playerSr = Art.CreateSprite("Body", playerTf, Art.HeroIdle, Color.white, 8);
            playerBody = playerSr.transform;
            targetRing = Art.CreateSprite("TargetRing", transform, Art.Ring, Palette.Target, 11).transform;

            enemies = new Pool<Enemy>(transform, "Enemy", 5);
            projectiles = new Pool<Projectile>(transform, "Projectile", 9);
            orbs = new Pool<XpOrb>(transform, "XpOrb", 2);
            fxs = new Pool<Fx>(transform, "Fx", 10);

            bestTime = PlayerPrefs.GetFloat(BestTimeKey, 0f);
            bestKills = PlayerPrefs.GetInt(BestKillsKey, 0);
            ResetRun();
            state = State.Title;
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
        }

        Camera SetupCamera()
        {
            var c = Camera.main;
            if (c == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                c = go.AddComponent<Camera>();
            }
            c.orthographic = true;
            c.orthographicSize = Tune.CameraSize;
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = Palette.Ground;
            c.transform.rotation = Quaternion.identity;
            return c;
        }

        void ResetRun()
        {
            enemies.Clear(); projectiles.Clear(); orbs.Clear(); fxs.Clear(); texts.Clear();
            mods = new PlayerMods();
            skills = new[] { new SkillState(0), new SkillState(1), new SkillState(2) };
            stacks.Clear(); cards.Clear();

            ppos = Vector2.zero; facing = Vector2.right;
            moveTarget = null; holdMove = false; attackTarget = null;
            walking = false; walkTime = 0f;
            hp = MaxHp;
            attackCd = attackLock = hurtFlash = eBuff = 0;
            attackCount = 0;

            elapsed = 0; spawnTimer = 0.5f; xp = 0; bannerTime = 0;
            eliteIndex = bossIndex = kills = pendingLevelUps = 0;
            level = 1; xpNeed = Tune.XpToNext(level);
            newRecord = false;
            Time.timeScale = 1f;
            SyncPlayerVisual();
        }

        void StartRun()
        {
            ResetRun();
            state = State.Playing;
        }

        void GoTitle()
        {
            ResetRun();
            state = State.Title;
        }

        void SetPaused(bool paused)
        {
            state = paused ? State.Paused : State.Playing;
            Time.timeScale = paused ? 0f : 1f;
        }

        void GameOver()
        {
            state = State.GameOver;
            Time.timeScale = 1f;
            newRecord = elapsed > bestTime;
            if (newRecord) { bestTime = elapsed; PlayerPrefs.SetFloat(BestTimeKey, bestTime); }
            if (kills > bestKills) { bestKills = kills; PlayerPrefs.SetInt(BestKillsKey, bestKills); }
            PlayerPrefs.Save();
            targetRing.gameObject.SetActive(false);
        }

        // ================================================================ 루프

        void Update()
        {
            switch (state)
            {
                case State.Title:
                case State.GameOver:
                    if (GameInput.KeyDown(KeyCode.Return) || GameInput.KeyDown(KeyCode.Space)) StartRun();
                    break;
                case State.Playing:
                    if (GameInput.KeyDown(KeyCode.Escape)) { SetPaused(true); break; }
                    Step(Mathf.Min(Time.deltaTime, 0.05f));
                    break;
                case State.Paused:
                    if (GameInput.KeyDown(KeyCode.Escape)) SetPaused(false);
                    break;
                case State.LevelUp:
                    for (int i = 0; i < cards.Count && i < 4; i++)
                        if (GameInput.KeyDown(KeyCode.Alpha1 + i)) { ChooseCard(i); break; }
                    break;
            }
        }

        // 카메라와 배경은 플레이어가 움직인 뒤(LateUpdate)에 따라가야 떨리지 않는다
        void LateUpdate()
        {
            cam.transform.position = new Vector3(ppos.x, ppos.y, -10f);
            grid.position = new Vector3(Mathf.Round(ppos.x), Mathf.Round(ppos.y), 0f);
        }

        void Step(float dt)
        {
            elapsed += dt;
            bannerTime -= dt;

            HandleCommands();
            UpdatePlayer(dt);
            UpdateSpawning(dt);
            UpdateEnemies(dt);
            UpdateProjectiles(dt);
            UpdateOrbs(dt);
            UpdateFx(dt);

            enemies.Sweep(); projectiles.Sweep(); orbs.Sweep(); fxs.Sweep();
            SyncPlayerVisual();

            if (hp <= 0) { GameOver(); return; }
            if (pendingLevelUps > 0) OpenLevelUp();
        }

        // ================================================================ 입력과 명령

        void HandleCommands()
        {
            Vector2 cursor = CursorWorld();

            // 우클릭: 이동. 누르고 있으면 커서를 계속 따라간다
            if (GameInput.RightClickDown())
            {
                attackTarget = null;
                moveTarget = cursor; holdMove = true;
                SpawnFx(cursor, Art.Circle, Palette.Marker, 0.25f, 0f, 0.35f, 12);
            }
            else if (holdMove && GameInput.RightClickHeld()) moveTarget = cursor;
            if (!GameInput.RightClickHeld()) holdMove = false;

            // 좌클릭: 그 적을 추격하며 자동 공격 (새 명령 전까지 유지). 적을 정확히 안 찍어도 커서 근처 적을 고른다
            if (GameInput.LeftClickDown())
            {
                Enemy e = EnemyAt(cursor) ?? Nearest(cursor, Tune.ClickAssist, null);
                if (e != null)
                {
                    attackTarget = e; attackSerial = e.serial;
                    moveTarget = null; holdMove = false;
                    SpawnFx(e.pos, Art.Ring, Palette.Target, e.radius * 2f + 0.5f, e.radius * 2f + 0.16f, 0.2f, 12);
                }
            }

            if (GameInput.KeyDown(KeyCode.Q)) CastQ(cursor);
            if (GameInput.KeyDown(KeyCode.W)) CastW();
            if (GameInput.KeyDown(KeyCode.E)) CastE(cursor);
        }

        Vector2 CursorWorld()
        {
            Vector3 screen = GameInput.MousePosition();
            screen.z = -cam.transform.position.z;
            return cam.ScreenToWorldPoint(screen);
        }

        // ================================================================ 플레이어

        void UpdatePlayer(float dt)
        {
            attackCd -= dt; attackLock -= dt; hurtFlash -= dt; eBuff -= dt;
            if (mods.regen > 0) Heal(mods.regen * dt);
            for (int i = 0; i < 3; i++) skills[i].cdLeft = Mathf.Max(0, skills[i].cdLeft - dt);

            Enemy target = Valid(attackTarget, attackSerial);
            // [임시] 공격하던 적이 죽으면 근처 적으로, 가만히 서 있으면 사거리 안의 적을 자동으로 공격한다
            if (target == null && attackTarget != null) target = Retarget(Range + 0.5f);
            if (target == null && !moveTarget.HasValue) target = Retarget(Range);

            Vector2? dest = null;
            if (target != null)
            {
                Vector2 to = target.pos - ppos;
                if (to.magnitude - target.radius > Range) dest = target.pos;
                else
                {
                    if (to.sqrMagnitude > 0.0001f) facing = to.normalized;
                    if (attackCd <= 0 && attackLock <= 0) FireBasic(target);
                }
            }
            else if (moveTarget.HasValue) dest = moveTarget.Value;

            // 평타 경직: 발사 직후 잠깐 이동 불가 (캔슬 불가)
            walking = false;
            if (dest.HasValue && attackLock <= 0)
            {
                walking = true;
                walkTime += dt;
                Vector2 to = dest.Value - ppos;
                float d = to.magnitude, step = MoveSpeed * dt;
                if (d <= step)
                {
                    ppos = dest.Value;
                    if (target == null) moveTarget = null;
                }
                else
                {
                    facing = to / d;
                    ppos += facing * step;
                }
            }
        }

        Enemy Retarget(float range)
        {
            Enemy e = Nearest(ppos, range, null);
            attackTarget = e;
            if (e != null) attackSerial = e.serial;
            return e;
        }

        void FireBasic(Enemy target)
        {
            float aspd = Aspd;
            attackCd = 1f / aspd;
            attackLock = Tune.AttackLock(aspd);
            attackCount++;

            float dmg = Attack;
            SpawnFx(GunTip(), Art.Circle, Palette.Muzzle, 0.16f, 0.04f, 0.08f, 12);
            SpawnBolt(target, dmg, 0f);
            if (mods.multishot > 0)
            {
                FindNearby(ppos, Range, target);
                for (int i = 0; i < mods.multishot && i < nearby.Count; i++) SpawnBolt(nearby[i], dmg, 0f);
            }
            if (mods.echoEvery > 0 && attackCount % mods.echoEvery == 0) SpawnBolt(target, dmg, 0.12f);
        }

        void DamagePlayer(float amount)
        {
            float dmg = Mathf.Max(1f, amount - mods.armor);
            hp -= dmg;
            hurtFlash = 0.12f;
            AddText(ppos + new Vector2(0, 0.3f), Mathf.RoundToInt(dmg).ToString(), Palette.Hurt, false);
        }

        void Heal(float amount)
        {
            hp = Mathf.Min(MaxHp, hp + amount);
        }

        float RollCrit(float dmg, out bool crit)
        {
            crit = Random.value < CritChance;
            return crit ? dmg * CritMult : dmg;
        }

        void SyncPlayerVisual()
        {
            playerTf.position = new Vector3(ppos.x, ppos.y, 0f);
            // 오른쪽을 보고 그린 그림이라 왼쪽을 볼 때 좌우 반전. 거의 위·아래로 움직일 때는 방향을 유지
            if (Mathf.Abs(facing.x) > 0.05f) playerSr.flipX = facing.x < 0;
            // 걸을 때: 두 프레임 다리 교차 + 살짝 통통 튀기
            bool step = walking && Mathf.FloorToInt(walkTime * 8f) % 2 == 1;
            playerSr.sprite = step ? Art.HeroStep : Art.HeroIdle;
            playerBody.localPosition = new Vector3(0f, walking ? Mathf.Abs(Mathf.Sin(walkTime * 16f)) * 0.03f : 0f, 0f);
            playerSr.color = hurtFlash > 0 ? Palette.Hurt : eBuff > 0 ? Palette.Haste : Color.white;

            Enemy t = Valid(attackTarget, attackSerial);
            targetRing.gameObject.SetActive(t != null && state == State.Playing);
            if (t != null)
            {
                targetRing.position = new Vector3(t.pos.x, t.pos.y, 0f);
                targetRing.localScale = Vector3.one * (t.radius * 2 + 0.16f);
            }
        }

        // ================================================================ 스킬 Q W E

        float SkillDamage(int index, float coef)
        {
            var s = skills[index];
            float evolve = s.evolved ? Tune.EvolveDamage[index] : 1f;
            float synergy = mods.synergyFinale ? Tune.SynergyDamage : 1f;
            return Attack * coef * evolve * (1 + mods.skillDmgPct) * synergy;
        }

        void StartCooldown(int index)
        {
            var s = skills[index];
            s.cdLeft = s.cdTotal = Tune.Cooldown(index, s.level, mods.SkillCdMult);
            // 삼위일체: 스킬을 쓰면 나머지 두 스킬 쿨다운 감소
            if (mods.synergyFinale)
                for (int k = 0; k < 3; k++)
                    if (k != index) skills[k].cdLeft = Mathf.Max(0, skills[k].cdLeft - Tune.SynergyCdCut);
        }

        static Vector2 DirOr(Vector2 v, Vector2 fallback)
        {
            return v.sqrMagnitude > 1e-6f ? v.normalized : fallback;
        }

        // Q 아케인 볼트: 커서 방향으로 적을 관통하는 마력탄
        void CastQ(Vector2 cursor)
        {
            var s = skills[0];
            if (s.level == 0 || s.cdLeft > 0) return;
            Vector2 dir = DirOr(cursor - ppos, facing);
            var p = NewProjectile(ProjKind.QBolt, ppos, dir * Tune.QSpeed, Tune.QRadius, SkillDamage(0, Tune.QDamage(s.level)),
                Tune.QRange / Tune.QSpeed, s.evolved ? Palette.Legendary : Palette.QBolt, 1.5f);
            p.evolved = s.evolved;
            facing = dir;
            StartCooldown(0);
        }

        // W 공허의 파동: 자기 중심 원형 피해 + 넉백 (진화하면 기절)
        void CastW()
        {
            var s = skills[1];
            if (s.level == 0 || s.cdLeft > 0) return;
            float radius = Tune.WRadius(s.level) + (s.evolved ? Tune.WEvolveRadius : 0f);
            float dmg = SkillDamage(1, Tune.WDamage(s.level));
            SpawnFx(ppos, Art.Ring, Palette.Void, 0.3f, radius * 2, 0.35f, 10);

            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, ppos, radius)) continue;
                float resist = e.IsBoss ? Tune.BossResist : 1f;
                e.knock += DirOr(e.pos - ppos, facing) * Tune.WKnockback * resist;
                if (s.evolved) e.stun = Mathf.Max(e.stun, Tune.WEvolveStun * resist);
                bool crit;
                float hit = RollCrit(dmg, out crit);
                DealDamage(e, hit, crit, 0);
            }
            StartCooldown(1);
        }

        // E 섬광 돌진: 커서 방향 대시를 14단계로 나눠 이동하며 경로의 적을 타격 (순간이동하면 적을 통과만 한다)
        void CastE(Vector2 cursor)
        {
            var s = skills[2];
            if (s.level == 0 || s.cdLeft > 0) return;
            Vector2 dir = DirOr(cursor - ppos, facing);
            float dist = Tune.EDistance(s.level);
            float dmg = SkillDamage(2, Tune.EDamage(s.level));
            Vector2 start = ppos;

            dashSet.Clear();
            dashing = true; dashKilled = false;
            for (int step = 1; step <= Tune.EDashSteps; step++)
            {
                Vector2 at = start + dir * (dist * step / Tune.EDashSteps);
                var list = enemies.Active;
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    if (!e.alive || dashSet.Contains(e) || !InReach(e, at, Tune.PlayerRadius + Tune.EHitPadding)) continue;
                    dashSet.Add(e);
                    bool crit;
                    float hit = RollCrit(dmg, out crit);
                    DealDamage(e, hit, crit, 0);
                }
                SpawnFx(at, Art.Circle, Palette.Dash, Tune.PlayerRadius * 2, 0.05f, 0.3f, 7);
            }
            dashing = false;

            ppos = start + dir * dist;
            facing = dir;
            moveTarget = null;
            attackLock = 0;
            eBuff = Tune.EBuffTime;
            StartCooldown(2);
            if (s.evolved && dashKilled) s.cdLeft *= 1f - Tune.EEvolveRefund;
        }

        // ================================================================ 적

        void UpdateSpawning(float dt)
        {
            spawnTimer -= dt;
            if (spawnTimer <= 0)
            {
                spawnTimer += Tune.SpawnInterval(elapsed);
                int n = Tune.SpawnCount(elapsed);
                for (int i = 0; i < n && enemies.Count < Tune.MaxEnemies; i++) SpawnNormal(PickKind(), false);
            }
            if (elapsed >= (eliteIndex + 1) * Tune.EliteEvery)
            {
                eliteIndex++;
                SpawnNormal(PickKind(), true);
            }
            if (elapsed >= (bossIndex + 1) * Tune.BossEvery)
            {
                bossIndex++;
                bool big = bossIndex % Tune.BigBossEveryNthBoss == 0;
                SpawnBoss(big);
                Announce(big ? "대형 보스 등장!" : "보스 등장!");
            }
        }

        // [임시] 원본의 적 종류별 등장 비율을 몰라서 정한 값
        EnemyKind PickKind()
        {
            float g = 60f, r = elapsed > 20 ? 28f : 0f, b = elapsed > 45 ? 14f : 0f;
            float x = Random.value * (g + r + b);
            if (x < g) return EnemyKind.Grunt;
            if (x < g + r) return EnemyKind.Runner;
            return EnemyKind.Brute;
        }

        Vector2 SpawnPoint()
        {
            float a = Random.value * Mathf.PI * 2f;
            return ppos + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(Tune.SpawnMin, Tune.SpawnMax);
        }

        void SpawnNormal(EnemyKind kind, bool elite)
        {
            var e = enemies.Get();
            float t = elapsed;
            e.rank = elite ? Rank.Elite : Rank.Normal;
            e.radius = kind.radius * (elite ? Tune.EliteRadius : 1f);
            e.maxHp = kind.hp * Tune.HpScale(t) * (elite ? Tune.EliteHp : 1f);
            e.damage = kind.damage * Tune.DamageScale(t) * (elite ? Tune.EliteDamage : 1f);
            e.speed = kind.speed * Tune.SpeedScale(t) * (elite ? Tune.EliteSpeed : 1f);
            e.xp = kind.xp * (elite ? Tune.EliteXp : 1);
            e.color = elite ? Palette.Elite : kind.color;
            InitEnemy(e, elite ? 6 : 5);
        }

        void SpawnBoss(bool big)
        {
            var e = enemies.Get();
            float t = elapsed;
            e.rank = big ? Rank.BigBoss : Rank.Boss;
            e.radius = big ? Tune.BigBossRadius : Tune.BossRadius;
            e.maxHp = big ? Tune.BigBossHp(t) : Tune.BossHp(t);
            e.damage = big ? Tune.BigBossDamage(t) : Tune.BossDamage(t);
            e.speed = big ? Tune.BigBossSpeed : Tune.BossSpeed;
            e.xp = big ? Tune.BigBossXp : Tune.BossXp;
            e.color = big ? Palette.BigBoss : Palette.Boss;
            InitEnemy(e, 7);
        }

        void InitEnemy(Enemy e, int order)
        {
            e.serial = ++serial;
            e.hp = e.maxHp;
            e.pos = SpawnPoint();
            e.knock = Vector2.zero;
            e.stun = e.flash = e.fade = 0f;
            e.contactReady = 0f;
            e.barrage = 1f;
            e.sr.sprite = Art.Circle;
            e.sr.sortingOrder = order;
            e.sr.color = e.color;
            e.tf.position = new Vector3(e.pos.x, e.pos.y, 0f);
            e.tf.localScale = Vector3.zero;
        }

        void UpdateEnemies(float dt)
        {
            var list = enemies.Active;
            float knockDecay = Mathf.Exp(-8f * dt);
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive) continue;

                Vector2 to = ppos - e.pos;
                float d = to.magnitude;
                if (e.stun > 0) e.stun -= dt;
                else if (d > 0.001f) e.pos += to / d * (e.speed * dt);
                e.pos += e.knock * dt;
                e.knock *= knockDecay;

                // 닿으면 피해. 같은 적은 ContactCooldown 마다 한 번
                if (d < Tune.PlayerRadius + e.radius && elapsed >= e.contactReady)
                {
                    e.contactReady = elapsed + Tune.ContactCooldown;
                    DamagePlayer(e.damage);
                }

                // 보스: 가까이 오면 사방으로 탄막
                if (e.IsBoss && d < Tune.BarrageRange)
                {
                    e.barrage -= dt;
                    if (e.barrage <= 0)
                    {
                        e.barrage = e.rank == Rank.BigBoss ? Tune.BigBossBarrageEvery : Tune.BossBarrageEvery;
                        Barrage(e);
                    }
                }

                // 무한 모드: 너무 멀어진 적은 회수해서 플레이어 근처에 다시 배치한다
                if (!e.IsBoss && d > Tune.RecycleDistance) { e.pos = SpawnPoint(); e.fade = 0f; }
            }

            Separate();

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive) continue;
                e.fade = Mathf.Min(1f, e.fade + dt * 4f);
                e.flash -= dt;
                e.tf.position = new Vector3(e.pos.x, e.pos.y, 0f);
                e.tf.localScale = Vector3.one * (e.radius * 2f * e.fade);
                e.sr.color = e.flash > 0 ? Color.white : e.stun > 0 ? Color.Lerp(e.color, Palette.Void, 0.5f) : e.color;
            }
        }

        // 적끼리 겹치지 않게 살짝 밀어낸다 [임시] (보스는 잘 안 밀린다)
        void Separate()
        {
            var list = enemies.Active;
            int n = list.Count;
            for (int i = 0; i < n; i++)
            {
                var a = list[i];
                if (!a.alive) continue;
                for (int j = i + 1; j < n; j++)
                {
                    var b = list[j];
                    if (!b.alive) continue;
                    Vector2 delta = b.pos - a.pos;
                    float min = a.radius + b.radius, sq = delta.sqrMagnitude;
                    if (sq >= min * min || sq < 1e-8f) continue;
                    float d = Mathf.Sqrt(sq);
                    Vector2 push = delta / d * ((min - d) * 0.5f);
                    float wa = a.IsBoss ? 0.1f : 1f, wb = b.IsBoss ? 0.1f : 1f, sum = wa + wb;
                    a.pos -= push * (2f * wa / sum);
                    b.pos += push * (2f * wb / sum);
                }
            }
        }

        void Barrage(Enemy boss)
        {
            int n = boss.rank == Rank.BigBoss ? Tune.BigBossShots : Tune.BossShots;
            float offset = Random.value * Mathf.PI * 2f;
            for (int k = 0; k < n; k++)
            {
                float a = offset + k * Mathf.PI * 2f / n;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                NewProjectile(ProjKind.EnemyBullet, boss.pos + dir * boss.radius, dir * Tune.BulletSpeed, Tune.BulletRadius,
                    boss.damage * Tune.BulletDamageRatio, Tune.BulletLife, Palette.Bullet, 1f);
            }
        }

        void DealDamage(Enemy e, float dmg, bool crit, int depth)
        {
            if (!e.alive) return;
            e.hp -= dmg;
            e.flash = 0.08f;
            AddText(e.pos + new Vector2(0, e.radius + 0.1f), Mathf.RoundToInt(dmg).ToString(), crit ? Palette.Crit : Palette.DamageText, crit);
            if (e.hp <= 0) Kill(e, depth);
        }

        // depth: 0 = 직접 공격, 1 = 폭발·연쇄 같은 2차 피해. 2차 피해로 죽은 적은 처치 폭발을 일으키지 않는다 (무한 연쇄 방지)
        void Kill(Enemy e, int depth)
        {
            e.alive = false;
            kills++;
            if (dashing) dashKilled = true;
            SpawnOrb(e.pos, e.xp);
            SpawnFx(e.pos, Art.Circle, Palette.Fade(e.color, 0.6f), e.radius * 2f, e.radius * 3.2f, 0.25f, 4);
            if (e.IsBoss)
            {
                SpawnFx(e.pos, Art.Ring, e.color, e.radius * 2f, e.radius * 8f, 0.6f, 10);
                Announce(e.rank == Rank.BigBoss ? "대형 보스 처치!" : "보스 처치!");
            }
            if (mods.killExplode > 0 && depth == 0)
                Explosion(e.pos, Tune.KillExplodeRadius, e.maxHp * mods.killExplode, 1, Palette.Explosion, null);
        }

        void Explosion(Vector2 at, float radius, float dmg, int depth, Color color, Enemy skip)
        {
            SpawnFx(at, Art.Circle, Palette.Fade(color, 0.45f), radius * 0.6f, radius * 2f, 0.22f, 4);
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || e == skip || !InReach(e, at, radius)) continue;
                DealDamage(e, dmg, false, depth);
            }
        }

        void Chain(Enemy from, float dmg)
        {
            chainSet.Clear();
            chainSet.Add(from);
            Enemy cur = from;
            for (int k = 0; k < Tune.ChainTargets; k++)
            {
                Enemy next = Nearest(cur.pos, Tune.ChainRange, chainSet);
                if (next == null) break;
                chainSet.Add(next);
                SpawnFx(Vector2.Lerp(cur.pos, next.pos, 0.5f), Art.Circle, Palette.Chain, 0.25f, 0.05f, 0.2f, 10);
                DealDamage(next, dmg, false, 1);
                cur = next;
            }
        }

        static bool InReach(Enemy e, Vector2 at, float radius)
        {
            float reach = radius + e.radius;
            return (e.pos - at).sqrMagnitude <= reach * reach;
        }

        static Enemy Valid(Enemy e, int serialNumber)
        {
            return e != null && e.alive && e.serial == serialNumber ? e : null;
        }

        Enemy Nearest(Vector2 at, float range, HashSet<Enemy> skip)
        {
            Enemy best = null;
            float bestSq = float.MaxValue;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || (skip != null && skip.Contains(e)) || !InReach(e, at, range)) continue;
                float sq = (e.pos - at).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        // 사거리 안의 다른 적들을 가까운 순으로 nearby 에 담는다
        void FindNearby(Vector2 at, float range, Enemy exclude)
        {
            nearby.Clear();
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.alive && e != exclude && InReach(e, at, range)) nearby.Add(e);
            }
            nearby.Sort((a, b) => (a.pos - at).sqrMagnitude.CompareTo((b.pos - at).sqrMagnitude));
        }

        Enemy EnemyAt(Vector2 point)
        {
            Enemy best = null;
            float bestSq = float.MaxValue;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.alive || !InReach(e, point, 0.15f)) continue;
                float sq = (e.pos - point).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        Enemy StrongestBoss()
        {
            Enemy best = null;
            var list = enemies.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e.alive && e.IsBoss && (best == null || e.maxHp > best.maxHp)) best = e;
            }
            return best;
        }

        // ================================================================ 투사체

        Projectile NewProjectile(ProjKind kind, Vector2 at, Vector2 vel, float radius, float damage, float life, Color color, float visualScale)
        {
            var p = projectiles.Get();
            p.kind = kind;
            p.pos = at; p.vel = vel; p.speed = vel.magnitude;
            p.radius = radius; p.damage = damage; p.life = life; p.delay = 0f;
            p.target = null; p.targetSerial = 0;
            p.pierceLeft = 0; p.bouncesLeft = 0; p.evolved = false;
            p.hit.Clear();   // 풀에서 꺼낼 때 꼭 비운다 (안 비우면 같은 적을 영원히 못 맞힌다)
            p.sr.sprite = Art.Circle;
            p.sr.color = color;
            p.sr.enabled = true;
            p.tf.position = new Vector3(at.x, at.y, 0f);
            p.tf.localScale = Vector3.one * (radius * 2f * visualScale);
            return p;
        }

        // 총구 위치 (캐릭터 그림 기준)
        Vector2 GunTip()
        {
            return ppos + new Vector2(playerSr.flipX ? -Art.HeroGunX : Art.HeroGunX, Art.HeroGunY);
        }

        void SpawnBolt(Enemy target, float dmg, float delay)
        {
            Vector2 from = GunTip();
            Vector2 dir = DirOr(target.pos - from, facing);
            var p = NewProjectile(ProjKind.Basic, from, dir * Tune.BoltSpeed, Tune.BoltRadius, dmg, Tune.BoltLife, Palette.Bolt, 1.8f);
            p.target = target; p.targetSerial = target.serial;
            p.pierceLeft = mods.pierce;
            p.bouncesLeft = mods.ricochet;
            p.delay = delay;
            p.sr.enabled = delay <= 0;
        }

        void UpdateProjectiles(float dt)
        {
            var list = projectiles.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (!p.alive) continue;

                if (p.delay > 0)
                {
                    p.delay -= dt;
                    p.pos = GunTip();
                    p.sr.enabled = p.delay <= 0;
                    p.tf.position = new Vector3(p.pos.x, p.pos.y, 0f);
                    continue;
                }

                p.life -= dt;
                if (p.life <= 0)
                {
                    if (p.kind == ProjKind.QBolt && p.evolved)
                        Explosion(p.pos, Tune.QExplodeRadius, p.damage * Tune.QExplodeRatio, 1, Palette.Legendary, null);
                    p.alive = false;
                    continue;
                }

                if (p.kind == ProjKind.Basic)
                {
                    // 평타는 대상을 따라간다 [임시]. 대상이 없어지면 직진
                    Enemy t = Valid(p.target, p.targetSerial);
                    if (t != null) p.vel = DirOr(t.pos - p.pos, p.vel) * p.speed;
                }
                p.pos += p.vel * dt;
                p.tf.position = new Vector3(p.pos.x, p.pos.y, 0f);

                if (p.kind == ProjKind.EnemyBullet)
                {
                    float reach = Tune.PlayerRadius + p.radius;
                    if ((ppos - p.pos).sqrMagnitude <= reach * reach) { DamagePlayer(p.damage); p.alive = false; }
                    continue;
                }

                var elist = enemies.Active;
                for (int k = 0; k < elist.Count && p.alive; k++)
                {
                    var e = elist[k];
                    if (!e.alive || p.hit.Contains(e) || !InReach(e, p.pos, p.radius)) continue;
                    if (p.kind == ProjKind.Basic) { OnBoltHit(p, e); break; }

                    // Q: 관통하며 닿는 모든 적을 한 번씩
                    p.hit.Add(e);
                    bool crit;
                    float hit = RollCrit(p.damage, out crit);
                    DealDamage(e, hit, crit, 0);
                }
            }
        }

        void OnBoltHit(Projectile p, Enemy e)
        {
            p.hit.Add(e);
            bool crit;
            float dmg = RollCrit(p.damage, out crit);
            DealDamage(e, dmg, crit, 0);
            if (mods.lifesteal > 0) Heal(dmg * mods.lifesteal);
            OnHitEffects(e, dmg, crit);

            // 튕김: 맞히지 않은 근처 적에게로 방향을 바꾼다
            if (p.bouncesLeft > 0)
            {
                Enemy next = Nearest(e.pos, Tune.RicochetRange, p.hit);
                if (next != null)
                {
                    p.bouncesLeft--;
                    p.target = next; p.targetSerial = next.serial;
                    return;
                }
            }
            // 관통: 그대로 직진
            if (p.pierceLeft > 0)
            {
                p.pierceLeft--;
                p.target = null;
                return;
            }
            p.alive = false;
        }

        // 온힛 효과: 평타가 맞았을 때만
        void OnHitEffects(Enemy e, float dmg, bool crit)
        {
            if (mods.explodeOnHit > 0)
                Explosion(e.pos, Tune.HitExplodeRadius, dmg * mods.explodeOnHit, 1, Palette.Explosion, e);
            if (mods.chainChance > 0 && Random.value < mods.chainChance)
                Chain(e, dmg * Tune.ChainRatio);
            if (crit && mods.critAftermath > 0)
                Explosion(e.pos, Tune.AftermathRadius, dmg * mods.critAftermath, 1, Palette.Crit, e);
        }

        // ================================================================ 경험치

        void SpawnOrb(Vector2 at, float value)
        {
            if (orbs.Count >= Tune.MaxOrbs)
            {
                // 구슬이 너무 많으면 기존 구슬에 합친다
                var list = orbs.Active;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].alive) { list[i].value += value; ScaleOrb(list[i]); return; }
            }
            var o = orbs.Get();
            o.pos = at + Random.insideUnitCircle * 0.08f;
            o.value = value;
            o.magnet = false;
            o.speed = 0f;
            o.sr.sprite = Art.Circle;
            o.sr.color = value >= 100 ? Palette.Legendary : Palette.Xp;
            o.tf.position = new Vector3(o.pos.x, o.pos.y, 0f);
            ScaleOrb(o);
        }

        static void ScaleOrb(XpOrb o)
        {
            o.tf.localScale = Vector3.one * Mathf.Clamp(0.07f + Mathf.Sqrt(o.value) * 0.02f, 0.09f, 0.4f);
        }

        void UpdateOrbs(float dt)
        {
            float magnet = Tune.XpMagnet * (1 + mods.magnetPct);
            var list = orbs.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (!o.alive) continue;
                Vector2 to = ppos - o.pos;
                float d = to.magnitude;
                if (d < magnet) o.magnet = true;
                if (o.magnet && d > 0.0001f)
                {
                    // [임시] 끌려오는 속도
                    o.speed += 9f * dt;
                    o.pos += to / d * Mathf.Min(d, (2f + o.speed) * dt);
                    d = (ppos - o.pos).magnitude;
                }
                if (d < Tune.XpPickup) { o.alive = false; GainXp(o.value); continue; }
                o.tf.position = new Vector3(o.pos.x, o.pos.y, 0f);
            }
        }

        void GainXp(float amount)
        {
            xp += amount;
            while (xp >= xpNeed)
            {
                xp -= xpNeed;
                level++;
                xpNeed = Tune.XpToNext(level);
                pendingLevelUps++;
            }
        }

        // ================================================================ 레벨업 · 증강 카드

        void OpenLevelUp()
        {
            RollCards();
            state = State.LevelUp;
            Time.timeScale = 0f;
            targetRing.gameObject.SetActive(false);
        }

        void RollCards()
        {
            cards.Clear();
            var pool = new List<Card>();
            var weights = new List<float>();
            var evolves = new List<Card>();

            foreach (var a in Augments.All)
            {
                int have;
                stacks.TryGetValue(a.id, out have);
                if (have >= a.maxStacks) continue;
                if (a.needsAllSkills && !AllSkillsLearned) continue;
                pool.Add(Card.ForAugment(a, have));
                weights.Add(Tune.RarityWeight[(int)a.rarity]);
            }
            for (int i = 0; i < 3; i++)
            {
                var s = skills[i];
                if (s.level == 0) { pool.Add(Card.Unlock(i)); weights.Add(Tune.SkillUnlockWeight); }
                else if (s.level < Tune.MaxSkillLevel) { pool.Add(Card.LevelUp(i, s.level, mods.SkillCdMult)); weights.Add(Tune.SkillLevelWeight); }
                else if (!s.evolved) evolves.Add(Card.Evolve(i));
            }

            // 진화 카드가 후보에 있으면 1장은 반드시 진화 카드
            if (evolves.Count > 0) cards.Add(evolves[Random.Range(0, evolves.Count)]);
            while (cards.Count < Tune.CardCount && pool.Count > 0)
            {
                int k = WeightedIndex(weights);
                cards.Add(pool[k]);
                pool.RemoveAt(k); weights.RemoveAt(k);
            }
            if (cards.Count == 0) cards.Add(Card.Heal());
        }

        static int WeightedIndex(List<float> weights)
        {
            float total = 0;
            for (int i = 0; i < weights.Count; i++) total += weights[i];
            float x = Random.value * total;
            for (int i = 0; i < weights.Count; i++)
            {
                x -= weights[i];
                if (x < 0) return i;
            }
            return weights.Count - 1;
        }

        void ChooseCard(int index)
        {
            if (state != State.LevelUp || index < 0 || index >= cards.Count) return;
            Card c = cards[index];
            switch (c.kind)
            {
                case CardKind.Augment:
                    int have;
                    stacks.TryGetValue(c.augment.id, out have);
                    stacks[c.augment.id] = have + 1;
                    // 최대 체력이 늘면 늘어난 만큼 현재 체력도 채운다
                    foreach (var fx in c.augment.effects) hp += AugmentApplier.Apply(mods, fx);
                    break;
                case CardKind.SkillUnlock: skills[c.skill].level = 1; skills[c.skill].cdLeft = 0; break;
                case CardKind.SkillLevel: skills[c.skill].level++; break;
                case CardKind.SkillEvolve: skills[c.skill].evolved = true; break;
                case CardKind.Heal: Heal(Tune.HealCard); break;
            }
            hp = Mathf.Min(hp, MaxHp);

            pendingLevelUps--;
            if (pendingLevelUps > 0) RollCards();
            else { state = State.Playing; Time.timeScale = 1f; }   // 시간 복구를 빠뜨리면 게임이 계속 멈춰 있다
        }

        // ================================================================ 효과

        void SpawnFx(Vector2 at, Sprite sprite, Color color, float fromSize, float toSize, float life, int order)
        {
            if (fxs.Count >= Tune.MaxFx) return;
            var f = fxs.Get();
            f.pos = at; f.age = 0f; f.life = life; f.from = fromSize; f.to = toSize; f.color = color;
            f.sr.sprite = sprite;
            f.sr.sortingOrder = order;
            f.sr.color = color;
            f.tf.position = new Vector3(at.x, at.y, 0f);
            f.tf.localScale = Vector3.one * fromSize;
        }

        void UpdateFx(float dt)
        {
            var list = fxs.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (!f.alive) continue;
                f.age += dt;
                float k = f.age / f.life;
                if (k >= 1f) { f.alive = false; continue; }
                f.tf.localScale = Vector3.one * Mathf.Lerp(f.from, f.to, k);
                f.sr.color = Palette.Fade(f.color, f.color.a * (1f - k));
            }

            for (int i = texts.Count - 1; i >= 0; i--)
            {
                var t = texts[i];
                t.age += dt;
                t.pos.y += 0.5f * dt;
                if (t.age >= FloatTextLife) texts.RemoveAt(i);
            }
        }

        void AddText(Vector2 at, string text, Color color, bool big)
        {
            if (texts.Count >= Tune.MaxTexts) texts.RemoveAt(0);
            texts.Add(new FloatText { pos = at + Random.insideUnitCircle * 0.06f, text = text, color = color, big = big });
        }

        void Announce(string text)
        {
            bannerText = text;
            bannerTime = 2.5f;
        }

        // ================================================================ 화면 (OnGUI)
        // 모든 좌표는 세로 720 기준 가상 좌표로 쓰고, 그릴 때 실제 화면 크기에 맞춰 늘린다.

        Font uiFont;
        GUIStyle style;
        float u = 1f, vw = 1280f;

        void OnGUI()
        {
            if (style == null)
            {
                // 유니티 기본 글꼴에는 한글이 없을 수 있어서 OS 글꼴을 쓴다
                uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Apple SD Gothic Neo", "AppleGothic", "Noto Sans CJK KR", "NanumGothic", "Arial" }, 16);
                style = new GUIStyle(GUI.skin.label) { font = uiFont, richText = false, clipping = TextClipping.Overflow };
            }
            u = Screen.height / 720f;
            vw = Screen.width / u;

            if (state != State.Title) DrawWorldText();
            if (state == State.Playing || state == State.Paused || state == State.LevelUp) DrawHud();

            switch (state)
            {
                case State.Title: DrawTitle(); break;
                case State.LevelUp: DrawLevelUp(); break;
                case State.Paused: DrawPause(); break;
                case State.GameOver: DrawGameOver(); break;
            }
        }

        Rect Scaled(Rect r)
        {
            return new Rect(r.x * u, r.y * u, r.width * u, r.height * u);
        }

        void Fill(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(Scaled(r), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        void Text(Rect r, string s, float size, Color c, TextAnchor anchor, bool bold = false, bool wrap = false, bool shadow = true)
        {
            style.fontSize = Mathf.Max(1, Mathf.RoundToInt(size * u));
            style.alignment = anchor;
            style.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            style.wordWrap = wrap;
            Rect sr = Scaled(r);
            if (shadow)
            {
                style.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.7f);
                GUI.Label(new Rect(sr.x + u, sr.y + u, sr.width, sr.height), s, style);
            }
            style.normal.textColor = c;
            GUI.Label(sr, s, style);
        }

        bool Button(Rect r, string label, Color color)
        {
            Rect sr = Scaled(r);
            bool hover = sr.Contains(Event.current.mousePosition);
            Fill(r, hover ? Color.Lerp(color, Color.white, 0.18f) : color);
            Text(r, label, 20, Color.white, TextAnchor.MiddleCenter, true);
            return GUI.Button(sr, GUIContent.none, GUIStyle.none);
        }

        static string FormatTime(float t)
        {
            int s = Mathf.FloorToInt(t);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        void DrawWorldText()
        {
            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i];
                Vector3 sp = cam.WorldToScreenPoint(new Vector3(t.pos.x, t.pos.y, 0f));
                float x = sp.x / u, y = (Screen.height - sp.y) / u;
                float a = 1f - t.age / FloatTextLife;
                Text(new Rect(x - 50, y - 14, 100, 28), t.text, t.big ? 20 : 14, Palette.Fade(t.color, a), TextAnchor.MiddleCenter, t.big);
            }
        }

        void DrawHud()
        {
            // 체력
            Fill(new Rect(20, 18, 280, 22), Palette.Panel);
            Fill(new Rect(22, 20, 276 * Mathf.Clamp01(hp / MaxHp), 18), Palette.HpBar);
            Text(new Rect(20, 18, 280, 22), "HP " + Mathf.Max(0, Mathf.CeilToInt(hp)) + " / " + Mathf.RoundToInt(MaxHp), 14, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(22, 44, 280, 24), "Lv " + level, 18, Palette.Xp, TextAnchor.MiddleLeft, true);

            // 시간 · 처치 · 최고 기록
            Text(new Rect(0, 8, vw, 38), FormatTime(elapsed), 30, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 44, vw, 22), "처치 " + kills, 16, Palette.Muted, TextAnchor.MiddleCenter);
            Text(new Rect(vw - 280, 18, 260, 22), "최고 기록 " + FormatTime(bestTime), 15, Palette.Muted, TextAnchor.MiddleRight);

            // 보스 체력
            Enemy b = StrongestBoss();
            if (b != null)
            {
                float w = Mathf.Min(520f, vw - 80f);
                Rect r = new Rect((vw - w) / 2f, 96, w, 16);
                Text(new Rect(r.x, r.y - 22, w, 20), b.rank == Rank.BigBoss ? "대형 보스" : "보스", 15, b.color, TextAnchor.MiddleCenter, true);
                Fill(r, Palette.Panel);
                Fill(new Rect(r.x + 2, r.y + 2, (w - 4) * Mathf.Clamp01(b.hp / b.maxHp), 12), b.color);
            }

            // 알림
            if (bannerTime > 0)
                Text(new Rect(0, 170, vw, 50), bannerText, 34, Palette.Fade(Palette.Legendary, Mathf.Clamp01(bannerTime)), TextAnchor.MiddleCenter, true);

            // 처음 몇 초 동안 조작 안내
            if (elapsed < 12f)
                Text(new Rect(0, 572, vw, 24), "우클릭: 이동   ·   좌클릭: 공격할 적 지정   ·   Q W E: 스킬 (레벨업 카드로 배움)   ·   Esc: 일시정지", 15, Palette.Muted, TextAnchor.MiddleCenter);

            DrawSkills();

            // 경험치
            Fill(new Rect(0, 710, vw, 10), Palette.Panel);
            Fill(new Rect(0, 710, vw * Mathf.Clamp01(xp / xpNeed), 10), Palette.Xp);
        }

        void DrawSkills()
        {
            const float size = 64f, gap = 14f;
            float x0 = (vw - (size * 3 + gap * 2)) / 2f, y = 618f;
            for (int i = 0; i < 3; i++)
            {
                var s = skills[i];
                Rect r = new Rect(x0 + i * (size + gap), y, size, size);
                Fill(r, Palette.Panel);
                if (s.level == 0)
                {
                    Text(r, SkillState.Keys[i], 26, Palette.Locked, TextAnchor.MiddleCenter, true);
                    Text(new Rect(r.x - 10, r.y + size + 2, size + 20, 18), "잠김", 12, Palette.Locked, TextAnchor.UpperCenter);
                    continue;
                }
                Color c = s.evolved ? Palette.Legendary : Palette.Skill;
                Fill(new Rect(r.x, r.y, size, 4), c);
                if (s.cdLeft > 0)
                {
                    float f = Mathf.Clamp01(s.cdLeft / s.cdTotal);
                    Fill(new Rect(r.x, r.y + size * (1 - f), size, size * f), new Color(0f, 0f, 0f, 0.55f));
                    Text(r, s.cdLeft.ToString("0.0"), 18, Color.white, TextAnchor.MiddleCenter, true);
                }
                else Text(r, SkillState.Keys[i], 26, Color.white, TextAnchor.MiddleCenter, true);
                Text(new Rect(r.x - 10, r.y + size + 2, size + 20, 18), s.evolved ? "진화" : "Lv " + s.level, 12, c, TextAnchor.UpperCenter, true);
            }
        }

        void DrawTitle()
        {
            Fill(new Rect(0, 0, vw, 720), Palette.Fade(Palette.Ground, 0.82f));
            Text(new Rect(0, 160, vw, 80), "ARCANE DRIFT", 64, Palette.Player, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 240, vw, 30), "무한 모드  ·  쓰러질 때까지 버티세요", 20, Palette.Body, TextAnchor.MiddleCenter);
            if (bestTime > 0)
                Text(new Rect(0, 280, vw, 26), "최고 기록  " + FormatTime(bestTime) + "   ·   최다 처치 " + bestKills, 17, Palette.Xp, TextAnchor.MiddleCenter);
            if (Button(new Rect(vw / 2f - 120, 340, 240, 56), "시작하기", Palette.ButtonPrimary)) StartRun();
            Text(new Rect(0, 402, vw, 22), "Enter 또는 Space 로도 시작", 14, Palette.Muted, TextAnchor.MiddleCenter);
            Text(new Rect(vw / 2f - 300, 460, 600, 120),
                "우클릭: 이동   ·   적을 좌클릭: 추격하며 자동 공격\nQ W E: 스킬 (레벨업 카드로 배우고 강화)\n1~4: 카드 선택   ·   Esc: 일시정지",
                16, Palette.Body, TextAnchor.UpperCenter, false, true);
        }

        void DrawLevelUp()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.72f));
            Text(new Rect(0, 100, vw, 50), "레벨 업!", 38, Palette.Xp, TextAnchor.MiddleCenter, true);
            string sub = "증강을 하나 고르세요  (클릭 또는 1~" + cards.Count + ")";
            if (pendingLevelUps > 1) sub += "   ·   남은 선택 " + pendingLevelUps;
            Text(new Rect(0, 150, vw, 26), sub, 16, Palette.Body, TextAnchor.MiddleCenter);

            const float gap = 18f, ch = 300f;
            int n = cards.Count;
            float cw = Mathf.Min(230f, (vw - 40f - gap * (n - 1)) / n);
            float x0 = (vw - (n * cw + (n - 1) * gap)) / 2f, y = 200f;
            for (int i = 0; i < n; i++)
            {
                Card c = cards[i];
                Rect r = new Rect(x0 + i * (cw + gap), y, cw, ch);
                bool hover = Scaled(r).Contains(Event.current.mousePosition);
                Fill(r, hover ? Palette.CardHover : Palette.Card);
                Fill(new Rect(r.x, r.y, cw, 6), c.color);
                Text(new Rect(r.x + 16, r.y + 20, cw - 32, 20), c.tag, 13, c.color, TextAnchor.MiddleLeft, true);
                Text(new Rect(r.x + 16, r.y + 44, cw - 32, 56), c.title, 20, Color.white, TextAnchor.UpperLeft, true, true);
                Text(new Rect(r.x + 16, r.y + 104, cw - 32, 150), c.desc, 15, Palette.Body, TextAnchor.UpperLeft, false, true, false);
                Text(new Rect(r.x, r.y + ch - 36, cw, 24), "[" + (i + 1) + "]", 15, Palette.Muted, TextAnchor.MiddleCenter);
                if (GUI.Button(Scaled(r), GUIContent.none, GUIStyle.none)) { ChooseCard(i); break; }
            }
        }

        void DrawPause()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.7f));
            Text(new Rect(0, 190, vw, 60), "일시정지", 44, Color.white, TextAnchor.MiddleCenter, true);
            float x = vw / 2f - 120;
            if (Button(new Rect(x, 290, 240, 54), "계속하기", Palette.ButtonPrimary)) SetPaused(false);
            if (Button(new Rect(x, 358, 240, 54), "다시 시작", Palette.ButtonSecondary)) StartRun();
            if (Button(new Rect(x, 426, 240, 54), "타이틀로", Palette.ButtonSecondary)) GoTitle();
        }

        void DrawGameOver()
        {
            Fill(new Rect(0, 0, vw, 720), new Color(0.04f, 0.03f, 0.08f, 0.75f));
            Text(new Rect(0, 140, vw, 60), "쓰러졌습니다", 48, Palette.Hurt, TextAnchor.MiddleCenter, true);
            if (newRecord) Text(new Rect(0, 202, vw, 30), "새 최고 기록!", 22, Palette.Legendary, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 240, vw, 36), "생존 시간  " + FormatTime(elapsed), 28, Color.white, TextAnchor.MiddleCenter, true);
            Text(new Rect(0, 282, vw, 26), "처치 " + kills + "   ·   레벨 " + level, 18, Palette.Body, TextAnchor.MiddleCenter);
            Text(new Rect(0, 314, vw, 24), "최고 기록 " + FormatTime(bestTime) + "   ·   최다 처치 " + bestKills, 16, Palette.Muted, TextAnchor.MiddleCenter);
            if (Button(new Rect(vw / 2f - 250, 380, 240, 56), "다시 시작", Palette.ButtonPrimary)) StartRun();
            if (Button(new Rect(vw / 2f + 10, 380, 240, 56), "타이틀로", Palette.ButtonSecondary)) GoTitle();
            Text(new Rect(0, 448, vw, 22), "Enter: 다시 시작", 14, Palette.Muted, TextAnchor.MiddleCenter);
        }
    }

    // ==================================================================== 수치

    static class Tune
    {
        // ---- 원본 수치 (arcane-drift.html, px ÷ 100) ----
        public const float CameraSize = 4.5f;

        public const float PlayerRadius = 0.15f;
        public const float PlayerSpeed = 2.5f;   // 원본 이속. 아래 PlayerSpeedMatchesMonsters 가 true 면 쓰지 않는다
        // 사용자 요청: 플레이어 이속 = 기본 몬스터(grunt) 이속. 몬스터 이속이 시간에 따라 오르면 같이 오른다.
        // false 로 바꾸면 원본 2.5 로 돌아간다
        public const bool PlayerSpeedMatchesMonsters = true;
        public const float PlayerHp = 100f;
        public const float PlayerDamage = 10f;
        public const float PlayerAspd = 1.1f;
        public const float PlayerRange = 2.55f;
        public const float CritChance = 0.05f;
        public const float CritMult = 1.6f;
        public const float BoltSpeed = 5.6f;
        public static float AttackLock(float aspd) { return Mathf.Min(0.22f, 0.4f / aspd); }

        public const float XpMagnet = 1.4f;
        public const float XpPickup = 0.34f;
        public static int XpToNext(int level) { return Mathf.RoundToInt(10 + level * 7 + level * level * 0.6f); }

        public const float SpawnMin = 5.6f, SpawnMax = 7.0f;
        public static float SpawnInterval(float t) { return Mathf.Max(0.16f, 1.05f - t * 0.002f); }
        public static int SpawnCount(float t) { return Mathf.Min(1 + Mathf.FloorToInt(t / 35f), 5); }
        public static float HpScale(float t) { return 1 + t / 50f; }
        public static float DamageScale(float t) { return 1 + t / 140f; }
        public static float SpeedScale(float t) { return 1 + Mathf.Min(t / 320f, 0.7f); }

        public const float EliteEvery = 22f;
        public const float EliteHp = 5f, EliteDamage = 1.8f, EliteSpeed = 0.85f, EliteRadius = 1.6f;
        public const int EliteXp = 6;

        public const float BossEvery = 90f;
        public static float BossHp(float t) { return 650f * (1 + t / 70f); }
        public static float BossDamage(float t) { return 20f * (1 + t / 200f); }
        public const float BossSpeed = 0.66f;
        public const int BossXp = 120, BossShots = 6;

        // 원본의 "6분 최종 보스". 무한 모드에서는 360초마다(보스 4번째마다) 나오고, 잡아도 게임이 계속된다.
        public const int BigBossEveryNthBoss = 4;
        public static float BigBossHp(float t) { return 2200f * (1 + t / 70f); }
        public static float BigBossDamage(float t) { return 34f * (1 + t / 200f); }   // [확인 필요] "피해 34 기준"의 증가식은 보스와 같다고 가정
        public const float BigBossSpeed = 0.58f;
        public const int BigBossXp = 300, BigBossShots = 10;

        public const float QSpeed = 6.2f;
        public static float QDamage(int lv) { return 1.8f + lv * 0.35f; }
        public static float WDamage(int lv) { return 1.2f + lv * 0.3f; }
        public static float EDamage(int lv) { return 1.1f + lv * 0.25f; }
        public static float WRadius(int lv) { return 1.5f + lv * 0.14f; }
        public static float EDistance(int lv) { return 2.3f + lv * 0.22f; }
        // 원문 "최소 1.2, × skillCdMult". 기본식은 Lv5에서도 1.6초 이상이라 최소값은 배율을 곱한 뒤에 적용해야 의미가 있다.
        public static float Cooldown(int skill, int lv, float mult)
        {
            float baseCd = skill == 0 ? 3.0f - lv * 0.28f : skill == 1 ? 6.5f - lv * 0.5f : 5.5f - lv * 0.45f;
            return Mathf.Max(1.2f, baseCd * mult);
        }
        public const int MaxSkillLevel = 5;
        public static readonly float[] EvolveDamage = { 1.6f, 1.5f, 1.5f };
        public const float WEvolveRadius = 0.7f, WEvolveStun = 0.6f;
        public const int EDashSteps = 14;
        public const float EBuffAspd = 0.6f, EBuffTime = 2f, EEvolveRefund = 0.7f;

        public const int CardCount = 4;
        public static readonly float[] RarityWeight = { 100f, 55f, 25f, 8f };

        // ---- 무한 모드 때문에 새로 생긴 수치 [임시] ----
        public const int MaxEnemies = 350;
        public const float RecycleDistance = 11f;

        // ---- 원본을 확인하지 못해 정한 수치 [임시] ----
        public const float ContactCooldown = 0.7f;
        public const float ClickAssist = 0.6f;   // 좌클릭이 적을 살짝 빗나가도 이 거리 안의 가장 가까운 적을 고른다
        public const float BoltRadius = 0.06f, BoltLife = 2f;
        public const float QRange = 6.5f, QRadius = 0.14f, QExplodeRadius = 0.9f, QExplodeRatio = 0.8f;
        public const float WKnockback = 4f;
        public const float EHitPadding = 0.12f;
        public const float BossRadius = 0.45f, BigBossRadius = 0.65f, BossResist = 0.15f;
        public const float BarrageRange = 4.5f, BossBarrageEvery = 2.2f, BigBossBarrageEvery = 1.8f;
        public const float BulletSpeed = 2.4f, BulletRadius = 0.08f, BulletDamageRatio = 0.5f, BulletLife = 5f;
        public const float SkillUnlockWeight = 150f, SkillLevelWeight = 90f;   // 증강 20여 종 사이에서 스킬 카드가 너무 드물게 나오지 않도록
        public const float HitExplodeRadius = 0.6f, AftermathRadius = 0.8f, KillExplodeRadius = 0.7f;
        public const float ChainRange = 1.8f, ChainRatio = 0.5f, RicochetRange = 2.5f;
        public const int ChainTargets = 3;
        public const float SynergyCdCut = 1f, SynergyDamage = 1.3f;
        public const float MinSkillCdMult = 0.4f;
        public const float HealCard = 30f;
        public const int MaxOrbs = 300, MaxTexts = 80, MaxFx = 260;
    }

    static class Palette
    {
        public static Color Hex(int rgb)
        {
            return new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);
        }
        public static Color Fade(Color c, float a) { return new Color(c.r, c.g, c.b, a); }

        public static readonly Color Ground = Hex(0x1A1626);
        public static readonly Color GridLine = Hex(0x2A243D);
        public static readonly Color Player = Hex(0xC9B6FF);
        public static readonly Color Marker = Hex(0x6BD5FF);
        public static readonly Color Muzzle = Hex(0xE4C8FF);
        public static readonly Color Haste = Hex(0xB8EEFF);
        public static readonly Color Target = Hex(0xFF6B6B);
        public static readonly Color Bolt = Hex(0xE4D8FF);
        public static readonly Color QBolt = Hex(0x6BD5FF);
        public static readonly Color Void = Hex(0x9B6BFF);
        public static readonly Color Dash = Fade(Hex(0x6BD5FF), 0.6f);
        public static readonly Color Elite = Hex(0xF0B429);
        public static readonly Color Boss = Hex(0xE0508C);
        public static readonly Color BigBoss = Hex(0xFF7A3D);
        public static readonly Color Bullet = Hex(0xFF6B9A);
        public static readonly Color Xp = Hex(0x5EE3A1);
        public static readonly Color Explosion = Hex(0xFF9F4A);
        public static readonly Color Chain = Hex(0x8FD3FF);
        public static readonly Color Crit = Hex(0xFFD166);
        public static readonly Color Hurt = Hex(0xFF5A5A);
        public static readonly Color DamageText = Color.white;

        public static readonly Color Panel = new Color(0.05f, 0.04f, 0.09f, 0.78f);
        public static readonly Color Card = Hex(0x231E36);
        public static readonly Color CardHover = Hex(0x2F2848);
        public static readonly Color Body = Hex(0xD6D0EA);
        public static readonly Color Muted = Hex(0x9A93B5);
        public static readonly Color Locked = Hex(0x5C5675);
        public static readonly Color HpBar = Hex(0xE5484D);
        public static readonly Color Skill = Hex(0x6BE3A8);
        public static readonly Color ButtonPrimary = Hex(0x6E56CF);
        public static readonly Color ButtonSecondary = Hex(0x3A3352);

        public static readonly Color[] Rarity = { Hex(0xA8B3C7), Hex(0x59C3FF), Hex(0xC38BFF), Hex(0xFFB84D) };
        public static readonly Color Legendary = Hex(0xFFB84D);
    }

    // ==================================================================== 적 종류

    class EnemyKind
    {
        public readonly string key;
        public readonly Color color;
        public readonly float radius, hp, damage, speed;
        public readonly int xp;

        EnemyKind(string key, Color color, float radius, float hp, float damage, float speed, int xp)
        {
            this.key = key; this.color = color; this.radius = radius; this.hp = hp; this.damage = damage; this.speed = speed; this.xp = xp;
        }

        public static readonly EnemyKind Grunt = new EnemyKind("grunt", Palette.Hex(0x7A4FB0), 0.13f, 16f, 6f, 0.95f, 2);
        public static readonly EnemyKind Runner = new EnemyKind("runner", Palette.Hex(0x3FA789), 0.10f, 10f, 4f, 1.65f, 2);
        public static readonly EnemyKind Brute = new EnemyKind("brute", Palette.Hex(0xB0554F), 0.19f, 44f, 12f, 0.70f, 5);
    }

    // ==================================================================== 풀링되는 것들

    class Pooled
    {
        public Transform tf;
        public SpriteRenderer sr;
        public bool alive;
    }

    enum Rank { Normal, Elite, Boss, BigBoss }

    class Enemy : Pooled
    {
        public int serial;   // 풀에서 다시 쓰일 때마다 바뀐다. 예전 대상을 쫓던 투사체가 새 적을 쫓지 않게 하려고
        public Rank rank;
        public Color color;
        public Vector2 pos, knock;
        public float radius, hp, maxHp, damage, speed;
        public int xp;
        public float contactReady, stun, flash, fade, barrage;
        public bool IsBoss { get { return rank == Rank.Boss || rank == Rank.BigBoss; } }
    }

    enum ProjKind { Basic, QBolt, EnemyBullet }

    class Projectile : Pooled
    {
        public ProjKind kind;
        public Vector2 pos, vel;
        public float speed, radius, damage, life, delay;
        public Enemy target;
        public int targetSerial, pierceLeft, bouncesLeft;
        public bool evolved;
        public readonly HashSet<Enemy> hit = new HashSet<Enemy>();
    }

    class XpOrb : Pooled
    {
        public Vector2 pos;
        public float value, speed;
        public bool magnet;
    }

    class Fx : Pooled
    {
        public Vector2 pos;
        public float age, life, from, to;
        public Color color;
    }

    class FloatText
    {
        public Vector2 pos;
        public string text;
        public Color color;
        public float age;
        public bool big;
    }

    class Pool<T> where T : Pooled, new()
    {
        readonly Stack<T> free = new Stack<T>();
        readonly Transform parent;
        readonly string name;
        readonly int order;
        public readonly List<T> Active = new List<T>();

        public Pool(Transform parent, string name, int order)
        {
            this.parent = parent; this.name = name; this.order = order;
        }

        public int Count { get { return Active.Count; } }

        public T Get()
        {
            T x;
            if (free.Count > 0) x = free.Pop();
            else
            {
                x = new T();
                x.sr = Art.CreateSprite(name, parent, Art.Circle, Color.white, order);
                x.tf = x.sr.transform;
            }
            x.alive = true;
            x.sr.gameObject.SetActive(true);
            Active.Add(x);
            return x;
        }

        // alive == false 인 것들을 한꺼번에 풀로 돌려보낸다
        public void Sweep()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                T x = Active[i];
                if (x.alive) continue;
                x.sr.gameObject.SetActive(false);
                int last = Active.Count - 1;
                Active[i] = Active[last];
                Active.RemoveAt(last);
                free.Push(x);
            }
        }

        public void Clear()
        {
            for (int i = 0; i < Active.Count; i++) Active[i].alive = false;
            Sweep();
        }
    }

    // ==================================================================== 스킬

    class SkillState
    {
        public static readonly string[] Keys = { "Q", "W", "E" };
        public static readonly string[] Names = { "아케인 볼트", "공허의 파동", "섬광 돌진" };

        public readonly int index;
        public int level;        // 0 = 잠김
        public bool evolved;
        public float cdLeft, cdTotal = 1f;

        public SkillState(int index) { this.index = index; }
    }

    // ==================================================================== 증강

    // 원본 player.mods 를 옮긴 것. 증강은 값을 더하기만 한다.
    // [확인 필요] 원본 필드 이름·구성을 확인하지 못해 증강 효과에 맞춰 정했다.
    class PlayerMods
    {
        public float damageAdd, damagePct, aspdPct, movePct, maxHpAdd, regen, rangeAdd;
        public float critChance, critMult, lifesteal, magnetPct, armor;
        public float skillCdCut, skillDmgPct;
        public int pierce, multishot, ricochet, echoEvery;
        public float explodeOnHit, chainChance, critAftermath, killExplode;
        public bool synergyFinale;

        public float SkillCdMult { get { return Mathf.Max(Tune.MinSkillCdMult, 1f - skillCdCut); } }
    }

    enum Rarity { Common, Advanced, Rare, Legendary }

    enum ModOp
    {
        DamageAdd, DamagePct, AspdPct, MovePct, MaxHp, Regen, RangeAdd,
        CritChance, CritMult, Lifesteal, MagnetPct, Armor, SkillCdCut, SkillDmgPct,
        Pierce, Multishot, Ricochet, Echo,
        ExplodeOnHit, Chain, CritAftermath, KillExplode, SynergyFinale,
    }

    struct ModEffect
    {
        public readonly ModOp op;
        public readonly float value;
        public ModEffect(ModOp op, float value) { this.op = op; this.value = value; }
    }

    class Augment
    {
        public readonly string id, name, desc;
        public readonly Rarity rarity;
        public readonly int maxStacks;
        public readonly List<ModEffect> effects;
        public bool needsAllSkills;

        public Augment(string id, string name, Rarity rarity, int maxStacks, string desc, params ModEffect[] effects)
        {
            this.id = id; this.name = name; this.rarity = rarity; this.maxStacks = maxStacks; this.desc = desc;
            this.effects = new List<ModEffect>(effects);
        }
    }

    static class AugmentApplier
    {
        // 증강 효과는 전부 여기 한 곳에서 적용한다.
        // 돌려주는 값: 최대 체력이 늘어난 양 (그만큼 현재 체력도 채워 주기 위해)
        public static float Apply(PlayerMods m, ModEffect fx)
        {
            float v = fx.value;
            switch (fx.op)
            {
                case ModOp.DamageAdd: m.damageAdd += v; break;
                case ModOp.DamagePct: m.damagePct += v; break;
                case ModOp.AspdPct: m.aspdPct += v; break;
                case ModOp.MovePct: m.movePct += v; break;
                case ModOp.MaxHp: m.maxHpAdd += v; return v;
                case ModOp.Regen: m.regen += v; break;
                case ModOp.RangeAdd: m.rangeAdd += v; break;
                case ModOp.CritChance: m.critChance += v; break;
                case ModOp.CritMult: m.critMult += v; break;
                case ModOp.Lifesteal: m.lifesteal += v; break;
                case ModOp.MagnetPct: m.magnetPct += v; break;
                case ModOp.Armor: m.armor += v; break;
                case ModOp.SkillCdCut: m.skillCdCut += v; break;
                case ModOp.SkillDmgPct: m.skillDmgPct += v; break;
                case ModOp.Pierce: m.pierce += (int)v; break;
                case ModOp.Multishot: m.multishot += (int)v; break;
                case ModOp.Ricochet: m.ricochet += (int)v; break;
                case ModOp.Echo: m.echoEvery = (int)v; break;
                case ModOp.ExplodeOnHit: m.explodeOnHit += v; break;
                case ModOp.Chain: m.chainChance += v; break;
                case ModOp.CritAftermath: m.critAftermath += v; break;
                case ModOp.KillExplode: m.killExplode += v; break;
                case ModOp.SynergyFinale: m.synergyFinale = true; break;
            }
            return 0f;
        }
    }

    // [확인 필요] 원본 AUGMENTS 배열을 확인하지 못해, 인수인계 문서의 이름·등급별 개수(일반 8, 고급 6, 희귀 6, 전설 5)에 맞춰 새로 만든 목록
    static class Augments
    {
        static ModEffect E(ModOp op, float v) { return new ModEffect(op, v); }

        public static readonly Augment[] All = Build();

        static Augment[] Build()
        {
            var finale = new Augment("synergyFinale", "삼위일체", Rarity.Legendary, 1,
                "Q·W·E를 모두 배웠을 때만 등장.\n스킬을 쓰면 나머지 두 스킬의 쿨다운이 1초 줄고, 스킬 피해 +30%",
                E(ModOp.SynergyFinale, 1));
            finale.needsAllSkills = true;

            return new[]
            {
                // 일반
                new Augment("sharp", "예리한 마력", Rarity.Common, 5, "공격력 +3", E(ModOp.DamageAdd, 3)),
                new Augment("quick", "빠른 손놀림", Rarity.Common, 5, "공격 속도 +12%", E(ModOp.AspdPct, 0.12f)),
                new Augment("swift", "질풍", Rarity.Common, 4, "이동 속도 +8%", E(ModOp.MovePct, 0.08f)),
                new Augment("sturdy", "강인함", Rarity.Common, 5, "최대 체력 +20", E(ModOp.MaxHp, 20)),
                new Augment("regen", "재생", Rarity.Common, 5, "초당 체력 회복 +1", E(ModOp.Regen, 1)),
                new Augment("eagle", "매의 눈", Rarity.Common, 3, "평타 사거리 +0.3", E(ModOp.RangeAdd, 0.3f)),
                new Augment("precise", "정밀", Rarity.Common, 5, "치명타 확률 +5%", E(ModOp.CritChance, 0.05f)),
                new Augment("magnet", "자석", Rarity.Common, 3, "경험치 흡수 범위 +30%", E(ModOp.MagnetPct, 0.3f)),
                // 고급
                new Augment("piercing", "날카로운 탄", Rarity.Advanced, 3, "평타가 적을 1명 더 관통합니다", E(ModOp.Pierce, 1)),
                new Augment("vamp", "흡혈", Rarity.Advanced, 3, "평타 피해의 3%만큼 체력 회복", E(ModOp.Lifesteal, 0.03f)),
                new Augment("amplify", "마력 증폭", Rarity.Advanced, 4, "공격력 +12%", E(ModOp.DamagePct, 0.12f)),
                new Augment("haste", "시간 단축", Rarity.Advanced, 3, "스킬 쿨다운 -10%", E(ModOp.SkillCdCut, 0.10f)),
                new Augment("deadly", "치명적 일격", Rarity.Advanced, 3, "치명타 피해 +35%", E(ModOp.CritMult, 0.35f)),
                new Augment("guard", "수호", Rarity.Advanced, 3, "받는 피해 -2 (최소 1)", E(ModOp.Armor, 2)),
                // 희귀 (온힛 효과)
                new Augment("blast", "폭발 탄환", Rarity.Rare, 2, "평타가 맞은 자리에서 터져 주변 적에게 피해의 40%", E(ModOp.ExplodeOnHit, 0.4f)),
                new Augment("chain", "연쇄 번개", Rarity.Rare, 2, "평타 적중 시 25% 확률로 근처 적 3명에게 번개 (피해의 50%)", E(ModOp.Chain, 0.25f)),
                new Augment("aftermath", "치명의 여파", Rarity.Rare, 1, "평타 치명타가 주변 적에게 충격파 (피해의 60%)", E(ModOp.CritAftermath, 0.6f)),
                new Augment("ricochet", "튕기는 탄", Rarity.Rare, 2, "평타가 근처 다른 적에게 2번 튕깁니다", E(ModOp.Ricochet, 2)),
                new Augment("twin", "쌍발", Rarity.Rare, 2, "평타를 쏠 때 사거리 안의 다른 적에게도 1발", E(ModOp.Multishot, 1)),
                new Augment("corpse", "처치 폭발", Rarity.Rare, 1, "처치한 적이 터져 주변에 그 적 최대 체력의 25% 피해", E(ModOp.KillExplode, 0.25f)),
                // 전설
                new Augment("echo", "메아리", Rarity.Legendary, 1, "평타 3번마다 같은 대상에게 한 발 더", E(ModOp.Echo, 3)),
                new Augment("surge", "비전 폭주", Rarity.Legendary, 1, "공격력 +25%, 공격 속도 +25%", E(ModOp.DamagePct, 0.25f), E(ModOp.AspdPct, 0.25f)),
                new Augment("warp", "시간 왜곡", Rarity.Legendary, 1, "스킬 쿨다운 -25%, 스킬 피해 +25%", E(ModOp.SkillCdCut, 0.25f), E(ModOp.SkillDmgPct, 0.25f)),
                new Augment("immortal", "불멸", Rarity.Legendary, 1, "최대 체력 +60, 초당 체력 회복 +3", E(ModOp.MaxHp, 60), E(ModOp.Regen, 3)),
                finale,
            };
        }
    }

    // ==================================================================== 카드

    enum CardKind { Augment, SkillUnlock, SkillLevel, SkillEvolve, Heal }

    class Card
    {
        static readonly string[] RarityNames = { "일반", "고급", "희귀", "전설" };

        public CardKind kind;
        public Augment augment;
        public int skill;
        public string title, tag, desc;
        public Color color;

        public static Card ForAugment(Augment a, int have)
        {
            string tag = RarityNames[(int)a.rarity] + (a.maxStacks > 1 ? "   " + have + " / " + a.maxStacks : "");
            return new Card { kind = CardKind.Augment, augment = a, title = a.name, tag = tag, desc = a.desc, color = Palette.Rarity[(int)a.rarity] };
        }

        public static Card Unlock(int i)
        {
            string[] what =
            {
                "커서 방향으로 적을 모두 꿰뚫는 마력탄",
                "주변 원형 범위에 피해를 주고 적을 밀쳐냄",
                "커서 방향으로 돌진하며 지나간 적을 공격. 사용 후 2초간 공격 속도 +0.6",
            };
            string desc = what[i] + "\n\n피해: 공격력 × " + Coef(i, 1).ToString("0.##") + "\n쿨다운 " + Tune.Cooldown(i, 1, 1f).ToString("0.#") + "초";
            return new Card { kind = CardKind.SkillUnlock, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i], tag = "새 스킬", desc = desc, color = Palette.Skill };
        }

        public static Card LevelUp(int i, int lv, float cdMult)
        {
            string desc = "Lv " + lv + " → " + (lv + 1)
                + "\n\n피해: 공격력 × " + Coef(i, lv).ToString("0.##") + " → " + Coef(i, lv + 1).ToString("0.##")
                + "\n쿨다운 " + Tune.Cooldown(i, lv, cdMult).ToString("0.#") + "초 → " + Tune.Cooldown(i, lv + 1, cdMult).ToString("0.#") + "초";
            if (i == 1) desc += "\n범위 " + Tune.WRadius(lv).ToString("0.##") + " → " + Tune.WRadius(lv + 1).ToString("0.##");
            if (i == 2) desc += "\n거리 " + Tune.EDistance(lv).ToString("0.##") + " → " + Tune.EDistance(lv + 1).ToString("0.##");
            return new Card { kind = CardKind.SkillLevel, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i], tag = "스킬 강화", desc = desc, color = Palette.Skill };
        }

        public static Card Evolve(int i)
        {
            string[] what =
            {
                "피해 ×1.6\n끝까지 날아가면 폭발합니다",
                "피해 ×1.5, 범위 +0.7\n맞은 적은 0.6초 기절",
                "피해 ×1.5\n돌진 중 적을 처치하면 쿨다운 70% 회복",
            };
            return new Card { kind = CardKind.SkillEvolve, skill = i, title = SkillState.Keys[i] + "  " + SkillState.Names[i] + " 진화", tag = "진화", desc = what[i], color = Palette.Legendary };
        }

        public static Card Heal()
        {
            return new Card { kind = CardKind.Heal, title = "회복", tag = "기본", desc = "체력 +" + Tune.HealCard + "\n(고를 수 있는 증강이 더 없을 때 나옵니다)", color = Palette.Rarity[0] };
        }

        static float Coef(int i, int lv)
        {
            return i == 0 ? Tune.QDamage(lv) : i == 1 ? Tune.WDamage(lv) : Tune.EDamage(lv);
        }
    }

    // ==================================================================== 입력

    // 프로젝트의 입력 설정(Old / New Input System / Both)이 무엇이든 똑같이 동작하게 감싼 헬퍼
    static class GameInput
    {
        public static bool RightClickDown()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(1);
#endif
        }

        public static bool LeftClickDown()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        public static bool RightClickHeld()
        {
#if USE_NEW_INPUT
            return Mouse.current != null && Mouse.current.rightButton.isPressed;
#else
            return Input.GetMouseButton(1);
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

        public static bool KeyDown(KeyCode key)
        {
#if USE_NEW_INPUT
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (key)
            {
                case KeyCode.Q: return kb.qKey.wasPressedThisFrame;
                case KeyCode.W: return kb.wKey.wasPressedThisFrame;
                case KeyCode.E: return kb.eKey.wasPressedThisFrame;
                case KeyCode.Alpha1: return kb.digit1Key.wasPressedThisFrame;
                case KeyCode.Alpha2: return kb.digit2Key.wasPressedThisFrame;
                case KeyCode.Alpha3: return kb.digit3Key.wasPressedThisFrame;
                case KeyCode.Alpha4: return kb.digit4Key.wasPressedThisFrame;
                case KeyCode.Return: return kb.enterKey.wasPressedThisFrame;
                case KeyCode.Space: return kb.spaceKey.wasPressedThisFrame;
                case KeyCode.Escape: return kb.escapeKey.wasPressedThisFrame;
                default: return false;
            }
#else
            return Input.GetKeyDown(key);
#endif
        }
    }

    // ==================================================================== 그림

    // 에셋 파일 없이 흰 원·고리·격자 그림을 코드로 만든다. 색은 SpriteRenderer.color 로 입힌다.
    // 미션 3(그래픽 적용)에서 이 부분을 실제 에셋으로 바꾼다.
    static class Art
    {
        public static Sprite Circle { get; private set; }
        public static Sprite Ring { get; private set; }
        public static Sprite HeroIdle { get; private set; }
        public static Sprite HeroStep { get; private set; }
        static Material spriteMaterial;   // URP 프로젝트일 때만 쓰는 조명 없는 재질 (2D 조명 없이도 보이게)

        public static void Init()
        {
            var unlit = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            spriteMaterial = unlit != null ? new Material(unlit) : null;
            Circle = MakeDisc(128, 0f);
            Ring = MakeDisc(128, 0.86f);
            HeroIdle = MakePixelSprite(Concat(HeroTop, HeroLegsIdle));
            HeroStep = MakePixelSprite(Concat(HeroTop, HeroLegsStep));
        }

        // ---- 플레이어 캐릭터: 16×20 도트, 오른쪽을 보고 있다 (탕탕특공대 느낌의 2등신, 직접 그린 그림) ----
        const int HeroW = 16, HeroH = 20;
        const float HeroPixelsPerUnit = 32f;          // 화면에서 0.5 × 0.625 유닛 (맞는 판정 원보다 크게 보이도록)
        const float HeroPivotY = 0.3f;                // 그림 아래에서 30% 지점이 플레이어 위치
        public const float HeroFeet = HeroH * HeroPivotY / HeroPixelsPerUnit;
        public const float HeroGunX = 7.5f / HeroPixelsPerUnit;   // 총구: 가운데에서 오른쪽 7.5픽셀
        public const float HeroGunY = -0.5f / HeroPixelsPerUnit;

        static readonly string[] HeroTop =
        {
            "................",
            ".....KKKKKK.....",
            "...KKHHHHHHKK...",
            "..KHHHHHHHHHHK..",
            ".KHHHHHHHHHHHHK.",
            "KRRRRRRRRRRRRRK.",
            "RRKHSSSSSSSSHK..",
            "KRKSSSSWESWESK..",
            ".KKSSSSWESWESK..",
            "..KSSppSSSSpSK..",
            "...KSSSSSSSSK...",
            "....KKKKKKKK....",
            "...KBBBBBBBK....",
            "..KBBYBBBBSKKKK.",
            "..KbBYBBBSGGGCCK",
            "..KbBBBBBKKgKKK.",
            "...KTTTTTK......",
        };
        static readonly string[] HeroLegsIdle =
        {
            "...KPPKPPK......",
            "...KOOKOOK......",
            "....KK.KK.......",
        };
        static readonly string[] HeroLegsStep =
        {
            "..KPPK.KPPK.....",
            "..KOOK.KOOK.....",
            "...KK...KK......",
        };

        static Color32 HeroColor(char c)
        {
            switch (c)
            {
                case 'K': return new Color32(27, 21, 48, 255);    // 외곽선
                case 'H': return new Color32(92, 58, 38, 255);    // 머리카락
                case 'R': return new Color32(229, 72, 77, 255);   // 머리띠
                case 'S': return new Color32(255, 214, 176, 255); // 피부
                case 'p': return new Color32(255, 160, 150, 255); // 볼
                case 'W': return new Color32(255, 255, 255, 255); // 눈 흰자
                case 'E': return new Color32(27, 21, 48, 255);    // 눈동자
                case 'B': return new Color32(62, 123, 214, 255);  // 옷
                case 'b': return new Color32(44, 90, 168, 255);   // 옷 그림자
                case 'Y': return new Color32(255, 201, 74, 255);  // 옷 장식
                case 'T': return new Color32(255, 201, 74, 255);  // 허리띠
                case 'G': return new Color32(110, 114, 132, 255); // 총
                case 'g': return new Color32(70, 72, 88, 255);    // 손잡이
                case 'C': return new Color32(190, 140, 255, 255); // 마력 총구
                case 'P': return new Color32(52, 46, 74, 255);    // 바지
                case 'O': return new Color32(120, 78, 48, 255);   // 신발
                default: return new Color32(0, 0, 0, 0);
            }
        }

        static string[] Concat(string[] a, string[] b)
        {
            var all = new string[a.Length + b.Length];
            a.CopyTo(all, 0);
            b.CopyTo(all, a.Length);
            return all;
        }

        // 글자 그림(위쪽 줄이 먼저)을 스프라이트로. 도트가 번지지 않게 Point 필터
        static Sprite MakePixelSprite(string[] rows)
        {
            var tex = new Texture2D(HeroW, HeroH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[HeroW * HeroH];
            for (int row = 0; row < HeroH; row++)
                for (int x = 0; x < HeroW; x++)
                    pixels[(HeroH - 1 - row) * HeroW + x] = HeroColor(rows[row][x]);   // 텍스처는 아래 줄부터
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, HeroW, HeroH), new Vector2(0.5f, HeroPivotY), HeroPixelsPerUnit);
        }

        public static SpriteRenderer CreateSprite(string name, Transform parent, Sprite sprite, Color color, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = order;
            if (spriteMaterial != null) sr.sharedMaterial = spriteMaterial;
            return sr;
        }

        // 지름(diameter) 유닛짜리 원
        public static Transform CreateCircle(string name, Transform parent, Color color, float diameter, int order)
        {
            var sr = CreateSprite(name, parent, Circle, color, order);
            sr.transform.localScale = Vector3.one * diameter;
            return sr.transform;
        }

        // extent×extent 유닛 크기로 깔리는 격자 배경
        public static Transform CreateGrid(Transform parent, float cell, float extent, Color ground, Color line)
        {
            var sr = CreateSprite("Grid", parent, MakeGridCell(cell, ground, line), Color.white, -10);
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.size = new Vector2(extent, extent);
            return sr.transform;
        }

        // 지름 1유닛짜리 흰 원(inner = 0) 또는 고리(inner = 구멍 반지름 비율)
        static Sprite MakeDisc(int px, float inner)
        {
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[px * px];
            float c = (px - 1) / 2f, r = px / 2f - 1;
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    if (inner > 0) a *= Mathf.Clamp01(d - inner * r + 0.5f);
                    pixels[y * px + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px);
        }

        // 한 칸(cell 유닛)짜리 그림: 배경색 위에 왼쪽·아래쪽 테두리 선
        static Sprite MakeGridCell(float cell, Color ground, Color line)
        {
            const int px = 64;
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                    pixels[y * px + x] = (x < 2 || y < 2) ? line : ground;
            tex.SetPixels(pixels);
            tex.Apply();
            // Tiled 그리기 모드는 Full Rect 메시가 필요하다
            return Sprite.Create(tex, new Rect(0, 0, px, px), new Vector2(0.5f, 0.5f), px / cell, 0, SpriteMeshType.FullRect);
        }
    }
}
