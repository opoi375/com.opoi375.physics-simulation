// Copyright (c) 2026 PhysicsSimulation. MIT License.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PhysicsSimulation.EditorTools
{
    /// <summary>
    /// 流体演示工具：围一个水箱、放一坨溃坝的水、把墙当碰撞体接进去。
    ///
    /// 为什么要有"水箱"这六块板：没有侧壁的话水一冲就散成一条线，
    /// 演示就变成了"粒子往下掉"，看不出流体与容器壁的关系。布局、参数推导、
    /// 粒子数预算全部拆成纯函数，这样它们能被断言，而不是靠人在编辑器里手摆一遍。
    /// </summary>
    public static class FluidDemoTools
    {
        public const int CreateMenuPriority = 130;
        public const int BuildMenuPriority = 131;
        public const int DumpMenuPriority = 132;

        public const string CreateMenuPath = "Tools/Physics Simulation/Fluid/Create Fluid Demo Scene";
        public const string BuildMenuPath = "Tools/Physics Simulation/Fluid/Build Fluid In Current Scene";
        public const string DumpMenuPath = "Tools/Physics Simulation/Fluid/Dump State";

        public const string ScenePath = "Assets/Scenes/FluidDemo.unity";

        /// <summary>
        /// 演示场景的粒子数软上限（超过就警告，不静默降质 —— v1.4.0 审计的教训）。
        ///
        /// 1500 不是拍的：基准实测 h = 2d、子步 1、迭代 2 时约 26.5 ms/千粒子·步
        /// （这台机器当时后台有个游戏占着 ~1.8 个核，空闲机器按软体基准的同一系数折算约 8 ms）。
        /// 再往上就是"每步一到三帧"，所以默认规模停在 1500，要更大水量请自行权衡帧率或等 Burst 后端。
        /// </summary>
        public const int DemoParticleBudget = 1500;

        /// <summary>演示水柱的粒子间距（米）。0.07 ⇒ 0.6×0.9×0.6 的水柱约 1053 个粒子。</summary>
        public const float DemoSpacing = 0.07f;

        /// <summary>演示水柱尺寸（米）：宽 × 高 × 深。</summary>
        /// <summary>演示水箱内空尺寸（宽 × 高 × 深）。 footprint 越小，同样的水越深。</summary>
        public static readonly Vector3 DemoTankInner = new Vector3(1.5f, 1.1f, 0.9f);

        /// <summary>演示壁厚（贴近水体那一侧的名义厚度）。墙会自动下探进地板、并在平面上互相搭接。</summary>
        public const float DemoWallThickness = 0.2f;

        /// <summary>
        /// 代理只往**背离水体的一侧**加厚的倍数（内表面位置逐位不变）。
        ///
        /// 这不是把墙做厚一点更结实，而是不这样做就会漏水：盒子代理把体内的点沿穿透最浅的那根
        /// 轴弹到体外，而一根薄板的中线只在内表面下方 t/2 = 0.1 m。实测水体拍地板时单步位移能到
        /// 0.15 m 以上，越过中线后弹出方向就变成板的另一面。取 2.5 倍后最薄一块板留 0.25 m 余量，
        /// 而速度上限×子步长 + 约束轨道×迭代数 = 0.203 m，这条余量关系由 FluidTankSealTests 钉住。
        /// </summary>
        public const float DemoOuterThicknessFactor = 2.5f;

        /// <summary>
        /// 溃坝水柱尺寸。注意 x/z 都比"内空减两个间隙"小，y 也留了余量：水柱绝不能**贴着**墙摆放，
        /// 盒子代理对"同时贴在多个面上"（到几个面的距离都是 0）的粒子只能按最浅的那根轴推出去，
        /// 方向是任意的 —— 实测整批粒子被从地板底下塞出去，质心掉到 y = −267 m。
        /// 间隙取一个粒子间距（见 DemoConfig 用例锁住这条）。
        /// </summary>
        public static readonly Vector3 DemoColumnSize = new Vector3(0.67f, 0.93f, 0.74f);

        /// <summary>
        /// 演示水柱的落点（世界）。DamBreak 的点阵是从局部原点往 +x/+z 长的，
        /// 所以把根挪到水箱 −x 角上、z 居中，水柱就贴着左壁、朝 +x 塌开 —— 这才是溃坝该有的样子，
        /// 摆在正中间的话两边对称扩散，看不出"坝"在哪。
        /// </summary>
        // 注意：FluidVolume.DamBreak 的 x/y 贴 0、z 是**居中**的（min.z = −depth/2）。
        // 这个 offset 以前是 z=−0.37，那是按"三个轴都贴最小角"想当然摆的，于是水柱有 560 个粒子
        // 生成在 −z 墙的身体里（当时的薄板代理把它们硬挤出来，正是"水泡在墙里"的源头之一）。
        public static readonly Vector3 DemoColumnOffset = new Vector3(-0.67f, 0.02f, 0f);

        /// <summary>相机注视点 / 机位方向 / 距离。方向必须是**俯视**：平视时前墙会把池底挡死，
        /// 截图里看不见水（实测踩过：1.2 m 高的墙 + y=0.6 的平视相机 ⇒ 一堵墙的照片）。</summary>
        public static readonly Vector3 DemoCameraFocus = new Vector3(0f, 0.15f, 0f);
        public static readonly Vector3 DemoCameraDirection = new Vector3(0.6f, 1.0f, -1.0f);
        public const float DemoCameraDistance = 3.4f;

        /// <summary>
        /// 演示水面体素 = 粒子间距 × 这个系数。0.75 是经得起看一眼的最低档：再大水面开始出棱，
        /// 再小就是按三次方涨成本。体素数 = 包围盒/体素，演示水箱大约 34×27×23 ≈ 2.1 万格。
        /// </summary>
        public const float DemoSurfaceCellFactor = 0.75f;

        /// <summary>演示水面阈值。场按静止点阵归一化，0.5 = “半个密度”，就是表面。</summary>
        public const float DemoSurfaceIsoLevel = 0.5f;

        /// <summary>演示水面每隔几帧重建一次。每帧重建在 1500 粒子上能跑，但没必要白扔一半帧时。</summary>
        public const int DemoSurfaceRefreshEveryNFrames = 2;

        /// <summary>演示水面体素预算（64³）。超了自己放大格子，不把内存吃掉。</summary>
        public const int DemoSurfaceMaxCells = FluidSurface.DefaultMaxCells;

        /// <summary>演示机位（纯函数，便于用单元测试锁住取景）。</summary>
        public static Vector3 DemoCameraPosition
        {
            get { return DemoCameraFocus + DemoCameraDirection.normalized * DemoCameraDistance; }
        }

        /// <summary>溃坝后水铺满箱底时的静水深（m）：体积 / 内底面积。</summary>
        public static float DemoPoolDepth
        {
            get
            {
                return DemoColumnSize.x * DemoColumnSize.y * DemoColumnSize.z
                       / (DemoTankInner.x * DemoTankInner.z);
            }
        }

        /// <summary>一块墙：中心 + 尺寸（米）。用 Cube 原语摆出来，自带 BoxCollider。</summary>
        public struct WallSpec
        {
            public Vector3 Center;
            public Vector3 Size;

            public WallSpec(Vector3 center, Vector3 size)
            {
                Center = center;
                Size = size;
            }
        }

        // ==================================================================
        // 纯函数
        // ==================================================================

        /// <summary>
        /// 水箱布局：底 + 四面侧墙 = 5 块，**故意没有顶盖**（有顶就成了封闭容器，看不出飞溅与浪高）。
        /// 地板的上表面正好在 y=0，内空是 (innerWidth × innerHeight × innerDepth) 且不伸进任何墙。
        /// </summary>
        public static WallSpec[] BuildTank(float innerWidth, float innerHeight, float innerDepth, float wallThickness)
        {
            if (!(innerWidth > 0f) || !(innerHeight > 0f) || !(innerDepth > 0f))
                throw new ArgumentOutOfRangeException("innerWidth", "水箱内空的三个维度都必须是正的");
            if (!(wallThickness > 0f))
                throw new ArgumentOutOfRangeException("wallThickness", "壁厚必须是正的（墙太薄会一步跨过去）");

            float t = wallThickness;
            // 代理厚度：内表面位置只由 halfW / halfD / innerHeight 决定，所以把 t 换成
            // T = t × DemoOuterThicknessFactor 时**内表面逐位不变**，变的只是往外、往下多出来那一块。
            // 多这一块的唯一目的就是让每块板的中线离水足够远：盒子代理把体内的点沿"穿透最浅的那根
            // 轴"弹出，一旦粒子被压过中线，弹出方向就翻到板的另一面（实测薄地板 10 步漏 44 个，加厚
            // 到 2.5t 后穿板 0 个、世界最低 y 从 -0.7 变成 +0.014）。
            float T = t * DemoOuterThicknessFactor;
            float halfW = innerWidth * 0.5f, halfD = innerDepth * 0.5f;

            // 墙还必须**下探到地板实体里**、并且**在平面上互相搭接**，否则桶照样是漏的：
            // 盒子代理把埋在体内的粒子沿"最浅的那根轴"推出去。水体被压向地板时，贴着地板
            // 边缘的那一层最浅轴是**侧向**而不是向上，于是被从地板侧边挤出去；如果墙的下边缘
            // 停在 y=0，它就正好掉进墙底下的空隙，一路自由落体 —— 实测漏到地板下方 65 m，
            // 速度 35.7 m/s（= √(2g·65)），画面看起来像"水自己炸开了"。
            // 所以墙高取 innerHeight + t、中心下沉 t/2（盖穿地板），四面墙在平面上都按
            // outer 尺寸互相盖住。
            float wallHeight = innerHeight + T;
            float wallCenterY = (innerHeight - T) * 0.5f;
            float outerW = innerWidth + 2f * T, outerD = innerDepth + 2f * T;

            return new[]
            {
                // 0: 地板，上表面贴 y=0
                new WallSpec(new Vector3(0f, -T * 0.5f, 0f), new Vector3(outerW, T, outerD)),
                // 1/2: ±x 侧墙（z 方向通长，盖住与 ±z 墙的竖直接缝）
                new WallSpec(new Vector3(-halfW - T * 0.5f, wallCenterY, 0f), new Vector3(T, wallHeight, outerD)),
                new WallSpec(new Vector3(halfW + T * 0.5f, wallCenterY, 0f), new Vector3(T, wallHeight, outerD)),
                // 3/4: ±z 侧墙（x 方向通长）
                new WallSpec(new Vector3(0f, wallCenterY, -halfD - T * 0.5f), new Vector3(outerW, wallHeight, T)),
                new WallSpec(new Vector3(0f, wallCenterY, halfD + T * 0.5f), new Vector3(outerW, wallHeight, T)),
                // 5: 顶盖。v1.5.0 原本故意不开顶（想看见飞溅），但 Play 里实测：溃坝浪头把水
                // 直接抛过 1.1 m 的墙头，300 帧后包围盒长到 10.3 × 9.1 × 6.7 m，一箱水泼在箱外，
                // 演示不可用（而主体仍是健康的：均值密度比 0.917、v_rms 2.6 m/s，不是求解器炸开）。
                // 顶盖内表面严格贴 y = innerHeight（仍然只往外侧加厚，所以内空逐位不变）；而它按
                // outer 尺寸整片盖住四面墙顶，所以缝也在实体里。观众看不到“墙”—— 水箱只有
                // Collider 没有 Mesh，开不开顶不影响取景，只影响水能不能跳出去。
                new WallSpec(new Vector3(0f, innerHeight + T * 0.5f, 0f), new Vector3(outerW, T, outerD)),
            };
        }

        /// <summary>
        /// 由粒子间距推一套能跑的流体参数：h = 2d（h = d 时核支撑里几乎没邻居，密度恒等于自身项）、
        /// 至少 2 子步（1 子步最容易漏碰撞）、拉力钳制开、涡度默认关（它要多扫一遍邻居表）。
        /// </summary>
        public static FluidParameters BuildParameters(float spacing)
        {
            if (!(spacing > 0f))
                throw new ArgumentOutOfRangeException("spacing", "粒子间距必须是正的");

            var p = new FluidParameters();
            p.particleSpacing = spacing;
            p.kernelRadius = spacing * 2f;
            p.substeps = 2;
            p.solverIterations = 2;
            p.clampTensileLambda = true;
            p.xsphViscosity = 0.05f;
            p.vorticityEpsilon = 0f;
            p.collisionThickness = spacing * 0.2f;
            // 速度上限不是美术选择，而是薄板不漏水的必要条件：单步平流位移 = v·dt_sub，加上轨道
            // 修正必须小于最薄一块板到中线的距离（见 FluidTankSealTests）。1.1 m 水头自由落体也才
            // 4.6 m/s，8 m/s 已经是留了余量的上限。
            p.maxSpeed = 8f;
            p.Validate();
            return p;
        }

        /// <summary>预算内不吭声；超了就报出**具体数字**（只说"太多了"等于没说）。</summary>
        public static string BudgetWarning(int particleCount, int budget)
        {
            if (particleCount <= budget) return "";
            return "粒子数 " + particleCount + " 超过预算 " + budget
                   + "：PBF 的成本随粒子数约三次方增长（邻居更多），请把 particleSpacing 调大或把水体调小";
        }

        // ==================================================================
        // 构建
        // ==================================================================

        /// <summary>
        /// 在 <paramref name="parent"/> 下放一坨溃坝水体。<paramref name="colliders"/> 为空时不开场景碰撞。
        /// 返回建出的水体个数（当前恒为 1 或 0，留多坨的扩展位）。
        /// </summary>
        public static int BuildFluid(Transform parent, IList<Collider> colliders,
                                     float width, float height, float depth, float spacing)
        {
            if (parent == null) return 0;

            var go = new GameObject("DamBreak");
            go.transform.SetParent(parent, false);

            var behaviour = go.AddComponent<FluidBehaviour>();
            behaviour.parameters = BuildParameters(spacing);
            behaviour.volumeShape = FluidVolumeShape.DamBreak;
            behaviour.volumeSize = new Vector3(width, height, depth);

            // 演示直接给水面模式：一堆蓝色小球看不出“水”，等值面才看得出浪与水面。
            // 粒子渲染开关保持原样（renderMode = Surface 时本来就不会提交粒子批次）。
            behaviour.renderMode = FluidRenderMode.Surface;
            behaviour.surfaceCellSize = spacing * DemoSurfaceCellFactor;
            behaviour.surfaceIsoLevel = DemoSurfaceIsoLevel;
            behaviour.surfaceRefreshEveryNFrames = DemoSurfaceRefreshEveryNFrames;
            behaviour.surfaceMaxCells = DemoSurfaceMaxCells;

            int count = Mathf.CeilToInt(width / spacing) * Mathf.CeilToInt(height / spacing)
                        * Mathf.CeilToInt(depth / spacing);
            string warning = BudgetWarning(count, DemoParticleBudget);
            if (warning.Length > 0) Debug.LogWarning("[PhysicsSimulation] " + warning);

            if (colliders != null && colliders.Count > 0)
            {
                behaviour.collideWithSceneColliders = true;
                var list = new List<Collider>();
                for (int i = 0; i < colliders.Count; i++)
                    if (colliders[i] != null) list.Add(colliders[i]);
                behaviour.sceneColliders = list;
            }

            behaviour.Rebuild();
            if (!behaviour.IsBuilt)
                Debug.LogWarning("[PhysicsSimulation] 流体没建起来：" + behaviour.LastBuildError);
            return behaviour.IsBuilt ? 1 : 0;
        }

        /// <summary>
        /// 水箱的“容器代理”：一个内侧盒子，水必须在它里面。半尺寸就是内空尺寸的一半，
        /// 中心在内空几何中心（地板顶面 y=0 ⇒ 中心 y = innerHeight/2）。
        ///
        /// 为什么不再拿六块实体板当碰撞体：盒子代理对体内的点是“沿穿透最浅的单轴脱出”，
        /// 板越厚坏半区越大，角部还会和相邻板互相推。实测（六面封桶、壁厚 0.5 m、maxSpeed=8，
        /// 300 步）：150 个粒子被挤进墙板、停在墙外表面内侧 1.4 cm（穿透 0.486 m，与 dt 无关，
        /// 100/400 步、三种 dt 数字一致），截图里就是贴在桶壁外侧的一条蓝带。
        /// 容器代理是对“可行域”的投影：越界的每个轴各自钉回内壁，凸集合上的最小位移投影，
        /// 没有中线、没有歧义、也不会从另一面出去。
        /// </summary>
        public static BoxContainerProxy BuildTankContainer(float innerWidth, float innerHeight, float innerDepth)
        {
            if (innerWidth <= 0f || innerHeight <= 0f || innerDepth <= 0f)
                throw new ArgumentOutOfRangeException("innerHeight", innerHeight, "内空三个尺寸必须为正");
            return new BoxContainerProxy(new Vector3(0f, innerHeight * 0.5f, 0f),
                                         new Vector3(innerWidth, innerHeight, innerDepth) * 0.5f,
                                         Quaternion.identity);
        }

        /// <summary>
        /// 把水箱内空写成 <see cref="FluidBehaviour"/> 的容器字段并重建一次。
        ///
        /// 为什么不是"往 Simulation.Collisions 里塞一个代理"：Rebuild 会换掉整个 Simulation 与它的
        /// CollisionSet，所以手工塞的代理在进 Play 模式（触发 OnEnable→Rebuild）时会被丢掉。
        /// 实测过：手工挂的容器在 Play 里"碰撞代理 0"，一箱水直落到 y = −92。容器必须是组件字段。
        /// </summary>
        public static void ApplyTankContainer(FluidBehaviour behaviour, float innerWidth, float innerHeight, float innerDepth)
        {
            if (behaviour == null) return;
            BoxContainerProxy container = BuildTankContainer(innerWidth, innerHeight, innerDepth);
            behaviour.enableBoxContainer = true;
            behaviour.containerCenter = container.Center;
            behaviour.containerHalfSize = container.HalfExtents;
            behaviour.Rebuild();
            if (!behaviour.IsBuilt)
                Debug.LogWarning("[PhysicsSimulation] 挂容器之后流体没建起来：" + behaviour.LastBuildError);
        }

        /// <summary>板的法向轴 = 三个轴里最薄的那个（水箱六块板都是薄板）。</summary>
        static int PlateNormalAxis(WallSpec plate)
        {
            var he = plate.Size * 0.5f;
            int axis = 0;
            if (he.y < he[axis]) axis = 1;
            if (he.z < he[axis]) axis = 2;
            return axis;
        }

        /// <summary>这块板朝外的法线：地板 −y、顶盖 +y、四面墙各朝自己离中心的那一侧。</summary>
        public static Vector3 PlateOutwardNormal(WallSpec plate)
        {
            int axis = PlateNormalAxis(plate);
            return new Vector3(
                axis == 0 ? (plate.Center.x >= 0f ? 1f : -1f) : 0f,
                axis == 1 ? (plate.Center.y >= 0f ? 1f : -1f) : 0f,
                axis == 2 ? (plate.Center.z >= 0f ? 1f : -1f) : 0f);
        }

        /// <summary>
        /// 这块板会不会挡在相机与水体之间（朝外法线指向相机那一侧）。
        ///
        /// 为什么把挡镜头的墙藏掉：内空 1.5 × 1.1 × 0.9 的桶，溃坝摊平后水深只有 0.26 m，池底
        /// 沉在 1.1 m 深的箱底。实测机位 (1.33, 2.36, −2.21) 到池心的连线在 z = −0.713（y=1.1，
        /// 正好是墙顶）就穿过了 −z 墙，于是截图里只有一个空盒子 —— 与当初「相机齐眼高度 + 水池
        /// 太浅」是同一个坑。把相机抬到 5 m 以上确实能看见，但演示就变成俯视图，浪头与水面形状
        /// 全压扁。板子现在是「只画不碰」（水由容器代理兜住），所以藏掉挡镜头那几块不动任何物理，
        /// 这是换容器方案顺带买到的自由度。
        /// </summary>
        public static bool PlateBlocksCamera(WallSpec plate, Vector3 cameraDirectionFromTank)
        {
            float len = cameraDirectionFromTank.magnitude;
            if (len <= 1e-6f) return false;      // 没有朝向可言：什么都不藏
            return Vector3.Dot(PlateOutwardNormal(plate), cameraDirectionFromTank / len) > 0.3f;
        }

        /// <summary>线段 a→b 是否穿过这块板（标准 slab 求交，含端点）。</summary>
        public static bool SegmentIntersectsPlate(Vector3 a, Vector3 b, WallSpec plate)
        {
            Vector3 min = plate.Center - plate.Size * 0.5f;
            Vector3 max = plate.Center + plate.Size * 0.5f;
            float tMin = 0f, tMax = 1f;
            for (int axis = 0; axis < 3; axis++)
            {
                float d = b[axis] - a[axis];
                float lo = min[axis], hi = max[axis];
                if (Mathf.Abs(d) < 1e-7f)
                {
                    if (a[axis] < lo || a[axis] > hi) return false;      // 与该层平行且在外面
                    continue;
                }
                float t1 = (lo - a[axis]) / d, t2 = (hi - a[axis]) / d;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
            return true;
        }

        /// <summary>
        /// 把水箱画出来：地板与不挡镜头的墙可见，顶盖与挡镜头的墙一律不画，全部**不带 Collider**
        /// （水由 <see cref="BuildTankContainer"/> 那个容器代理兜住，实体板当碰撞体会踩
        /// 「最近面投影」的坑，见上面那段实测数字）。
        /// </summary>
        public static void CreateTank(WallSpec[] walls, Transform parent, Vector3 cameraDirectionFromTank)
        {
            for (int i = 0; i < walls.Length; i++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = i == 0 ? "Tank_Floor" : (i == walls.Length - 1 ? "Tank_Lid" : "Tank_Wall_" + i);
                wall.transform.SetParent(parent, false);
                wall.transform.position = walls[i].Center;
                wall.transform.localScale = walls[i].Size;
                var collider = wall.GetComponent<Collider>();
                if (collider != null) UnityEngine.Object.DestroyImmediate(collider);        // 只画不碰
                bool isLid = i == walls.Length - 1;
                if (isLid || PlateBlocksCamera(walls[i], cameraDirectionFromTank))
                {
                    var renderer = wall.GetComponent<Renderer>();
                    if (renderer != null) UnityEngine.Object.DestroyImmediate(renderer);    // 藏掉
                }
            }
        }

        // ==================================================================
        // 菜单
        // ==================================================================

        [MenuItem(CreateMenuPath, false, CreateMenuPriority)]
        public static void CreateFluidDemoScene()
        {
            // 静默存盘：EditorSceneManager.SaveOpenScenes() 对**没有文件路径**的场景会弹系统
            // "保存场景"对话框，从 ExecuteMenuItem / REST 里调用时没人点那个框，主线程就永久堵死
            // （实测 7 分钟无响应）。一律走 DemoSceneSave，它只存有路径的场景。
            if (!DemoSceneSave.SaveOpenScenesWithoutPrompting())
                Debug.LogWarning("[PhysicsSimulation] 有未保存且没有路径的场景，仍继续生成流体演示场景。");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var tankRoot = new GameObject("Tank");
            var walls = BuildTank(DemoTankInner.x, DemoTankInner.y, DemoTankInner.z, DemoWallThickness);
            CreateTank(walls, tankRoot.transform, DemoCameraDirection);
            var colliders = new List<Collider>();           // 水箱不给 Collider，靠容器代理兜水

            var fluidRoot = new GameObject("FluidDemo");
            fluidRoot.transform.position = DemoColumnOffset;
            int built = BuildFluid(fluidRoot.transform, colliders,
                DemoColumnSize.x, DemoColumnSize.y, DemoColumnSize.z, DemoSpacing);

            ApplyTankContainer(fluidRoot.GetComponentInChildren<FluidBehaviour>(),
                DemoTankInner.x, DemoTankInner.y, DemoTankInner.z);

            SetupCameraAndLight(DemoCameraFocus, DemoCameraDirection, DemoCameraDistance);

            AssetDatabase.Refresh();
            EditorSceneManager.SaveScene(scene, ScenePath);   // 显式路径，不弹框

            var behaviour = fluidRoot.GetComponentInChildren<FluidBehaviour>();
            var sb = new StringBuilder();
            sb.Append("[PhysicsSimulation] 流体演示场景已生成：").Append(ScenePath)
              .Append("（").Append(built).Append(" 坨溃坝水，水箱 ").Append(walls.Length).Append(" 块板只画不碰，水由 1 个内侧盒子容器代理兜住");
            if (behaviour != null && behaviour.IsBuilt)
            {
                var sim = behaviour.Simulation;
                sb.Append("；粒子 ").Append(sim.ParticleCount)
                  .Append(" 个（预算 ").Append(DemoParticleBudget)
                  .Append("，间距 ").Append(DemoSpacing).Append(" m）")
                  .Append("、平均邻居 ").Append(sim.AverageNeighborDegree.ToString("0.0"))
                  .Append("、最大邻居 ").Append(sim.MaxNeighborDegree)
                  .Append("、粒子质量 ").Append(sim.ParticleMass.ToString("0.0000"))
                  .Append(" kg、碰撞代理 ").Append(sim.Collisions.Count).Append(" 个");
            }
            sb.Append("）。播放后水柱会往 +x 塌开、被侧墙挡住；")
              .Append("规模上限的依据是基准实测（见文档「流体 / 性能」），把 spacing 调小换画质就是线性掉帧；")
              .Append("出问题时用 ").Append(DumpMenuPath).Append(" 看每坨的密度与包围盒。");
            Debug.Log(sb.ToString());
        }

        [MenuItem(BuildMenuPath, false, BuildMenuPriority)]
        public static void BuildFluidInCurrentScene()
        {
            var tankRoot = new GameObject("Tank");
            var tankWalls = BuildTank(DemoTankInner.x, DemoTankInner.y, DemoTankInner.z, DemoWallThickness);
            CreateTank(tankWalls, tankRoot.transform, DemoCameraDirection);
            var colliders = new List<Collider>();
            var fluidRoot = new GameObject("FluidDemo");
            fluidRoot.transform.position = DemoColumnOffset;
            int built = BuildFluid(fluidRoot.transform, colliders,
                DemoColumnSize.x, DemoColumnSize.y, DemoColumnSize.z, DemoSpacing);
            ApplyTankContainer(fluidRoot.GetComponentInChildren<FluidBehaviour>(),
                DemoTankInner.x, DemoTankInner.y, DemoTankInner.z);
            Debug.Log("[PhysicsSimulation] 已在当前场景生成 " + built + " 坨溃坝水（不写盘）。");
        }

        [MenuItem(DumpMenuPath, false, DumpMenuPriority)]
        public static void DumpFluidState()
        {
            var behaviours = UnityEngine.Object.FindObjectsOfType<FluidBehaviour>();
            if (behaviours.Length == 0)
            {
                Debug.Log("[PhysicsSimulation] 场景里没有 FluidBehaviour。");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[PhysicsSimulation] 流体状态 dump（").Append(behaviours.Length).Append(" 坨）\n");
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                sb.Append("— ").Append(Describe(behaviour)).Append('\n');
                if (!behaviour.IsBuilt) continue;

                var sim = behaviour.Simulation;
                var bounds = sim.Bounds();
                float meanDensity = 0f, minDensity = float.MaxValue, maxDensity = 0f;
                for (int p = 0; p < sim.ParticleCount; p++)
                {
                    float d = sim.GetDensity(p);
                    meanDensity += d;
                    minDensity = Mathf.Min(minDensity, d);
                    maxDensity = Mathf.Max(maxDensity, d);
                }
                meanDensity /= Mathf.Max(1, sim.ParticleCount);

                sb.Append("    粒子 ").Append(sim.ParticleCount)
                  .Append("，平均邻居 ").Append(sim.AverageNeighborDegree.ToString("0.0"))
                  .Append("，最大邻居 ").Append(sim.MaxNeighborDegree).Append('\n')
                  .Append("    密度 均值 ").Append(meanDensity.ToString("0.0"))
                  .Append(" / 最小 ").Append(minDensity.ToString("0.0"))
                  .Append(" / 最大 ").Append(maxDensity.ToString("0.0"))
                  .Append("（静止 ").Append(sim.Parameters.restDensity.ToString("0"))
                  .Append(" ⇒ 均值比 ").Append((meanDensity / sim.Parameters.restDensity).ToString("0.000")).Append("）\n")
                  .Append("    包围盒 中心 ").Append(Fmt(bounds.center)).Append(" 尺寸 ").Append(Fmt(bounds.size)).Append('\n')
                  .Append("    质心 ").Append(Fmt(sim.CenterOfMass()))
                  .Append("，平均涡量 ").Append(sim.AverageVorticity().ToString("0.000"))
                  .Append("，动能 ").Append(sim.TotalKineticEnergy().ToString("0.000"))
                  .Append("，质量 ").Append(sim.TotalMass().ToString("0.000")).Append('\n')
                  .Append("    碰撞代理 ").Append(sim.Collisions.Count)
                  .Append("，子步 ").Append(sim.Parameters.substeps)
                  .Append("，迭代 ").Append(sim.Parameters.solverIterations)
                  .Append("，XSPH ").Append(sim.Parameters.xsphViscosity)
                  .Append("，涡度 ").Append(sim.Parameters.vorticityEpsilon)
                  .Append("，拉力钳制 ").Append(sim.Parameters.clampTensileLambda ? "开" : "关").Append('\n');

                // 这一行是排查“水明明在箱里却看不见”的入口：等值面没生成 / 网格空 / 节流没触发，
                // 在粒子模式下全都看不出来
                sb.Append("    渲染 ").Append(behaviour.renderMode)
                  .Append("，水面 三角 ").Append(behaviour.SurfaceTriangleCount)
                  .Append("、重建 ").Append(behaviour.SurfaceRevision)
                  .Append(" 次、网格 ").Append(behaviour.SurfaceMesh == null ? "无" : "有")
                  .Append("，粒子批次 ").Append(behaviour.ParticleBatchCount).Append('\n');
            }
            Debug.Log(sb.ToString());
        }

        static string Describe(FluidBehaviour behaviour)
        {
            if (behaviour.IsBuilt) return behaviour.name;
            return behaviour.name + (string.IsNullOrEmpty(behaviour.LastBuildError)
                ? "（尚未构建：EditMode 下没跑过 Update，或刚退出 Play）"
                : "（构建失败：" + behaviour.LastBuildError + "）");
        }

        static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + ", " + v.y.ToString("0.000") + ", " + v.z.ToString("0.000") + ")";
        }

        static void SetupCameraAndLight(Vector3 focus, Vector3 direction, float distance)
        {
            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            cameraObject.transform.position = focus + direction.normalized * distance;
            cameraObject.transform.LookAt(focus);

            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
        }
    }
}
