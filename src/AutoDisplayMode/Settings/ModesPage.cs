using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 「模式」分頁：新增（一般模式或潛行模式）、刪除、重新命名、複製模式，設定圖示顏色、手動切換進入時的自動切換、
    /// 未記錄的螢幕（一般模式）或虛擬螢幕的解析度與更新率（潛行模式），以及在視窗中擷取（結果先存進副本，套用時存入的模式成為目前模式）。
    /// 虛擬螢幕驅動不可用時，潛行模式的設定中顯示警告與「安裝驅動」按鈕（與一般設定分頁共用 <see cref="DriverController"/>）。
    /// </summary>
    sealed class ModesPage : SettingsPage
    {
        readonly AppCoordinator app;
        readonly DriverController driver;
        readonly ListBox list;
        readonly TextBox name;
        readonly Label nameError;
        readonly FlowLayoutPanel colors;
        readonly ComboBox manualAutoSwitch;
        readonly ComboBox unlisted;
        readonly Label monitorsSummary;
        readonly Label captureNote;
        readonly Label captureBlocked;
        readonly Button captureButton;
        readonly Button captureNewButton;
        readonly ToolTip tips = new();
        readonly ComboBox virtualResolution;
        readonly ComboBox virtualRate;
        readonly Label driverWarning;
        readonly Label driverDetail;
        readonly Button installDriver;
        readonly List<Control> normalOnly = new();   // 只有一般模式顯示的欄位
        readonly List<Control> remoteOnly = new();   // 只有潛行模式顯示的欄位
        readonly Button removeButton;
        readonly Button duplicateButton;
        readonly Button upButton;
        readonly Button downButton;
        int dragFrom = -1;        // 按下滑鼠的模式位置（拖拉排序用）；沒有按下時為 -1
        Point dragStart;
        int insertAt = -1;        // 拖拉中的插入位置（插在這個位置之前，可等於模式數）；沒有拖拉時為 -1
        readonly Panel details;
        readonly TableLayoutPanel detailsTable;
        AppConfig draft = new();
        bool showing;   // 正在把選取的模式顯示到欄位上：欄位改變不寫回副本

        /// <summary>虛擬螢幕解析度下拉選單的項目。</summary>
        sealed record Resolution(int Width, int Height)
        {
            public override string ToString() => Width + " × " + Height;
        }

        /// <summary>虛擬螢幕更新率下拉選單的項目。</summary>
        sealed record Rate(int Hz)
        {
            public override string ToString() => Hz + " Hz";
        }

        public ModesPage(AppCoordinator app, DriverController driver) : base("模式")
        {
            this.app = app;
            this.driver = driver;

            // ---- 左邊：模式清單與按鈕 ----
            list = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
            };
            list.DrawItem += DrawModeItem;
            list.SelectedIndexChanged += delegate { ShowSelected(); };

            var addButton = ListButton("新增");
            duplicateButton = ListButton("複製");
            removeButton = ListButton("刪除");
            addButton.Click += delegate { AddMode(); };
            duplicateButton.Click += delegate { DuplicateMode(); };
            removeButton.Click += delegate { RemoveMode(); };
            upButton = ListButton("上移");
            downButton = ListButton("下移");
            upButton.Click += delegate { MoveSelected(-1); };
            downButton.Click += delegate { MoveSelected(+1); };
            var addRemoteButton = ListButton("新增潛行模式（虛擬螢幕）");
            addRemoteButton.Click += delegate { AddRemoteMode(); };
            captureNewButton = ListButton("以目前螢幕狀態新增模式");
            captureNewButton.Click += delegate { CaptureToNewMode(); };

            // 拖拉清單中的模式調整順序
            list.AllowDrop = true;
            list.MouseDown += OnListMouseDown;
            list.MouseMove += OnListMouseMove;
            list.DragOver += OnListDragOver;
            list.DragLeave += delegate { SetInsertAt(-1); };
            list.DragDrop += OnListDragDrop;

            // 左欄：清單在上，下方一列三個等寬按鈕（新增、複製、刪除），一列上移、下移，再下方兩個整列寬的按鈕
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 5, Margin = new Padding(0, 0, 12, 0) };
            for (int i = 0; i < 3; i++) left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(list, 0, 0);
            left.SetColumnSpan(list, 3);
            left.Controls.Add(addButton, 0, 1);
            left.Controls.Add(duplicateButton, 1, 1);
            left.Controls.Add(removeButton, 2, 1);
            left.Controls.Add(upButton, 0, 2);
            left.Controls.Add(downButton, 1, 2);
            left.Controls.Add(addRemoteButton, 0, 3);
            left.SetColumnSpan(addRemoteButton, 3);
            left.Controls.Add(captureNewButton, 0, 4);
            left.SetColumnSpan(captureNewButton, 3);
            tips.SetToolTip(addRemoteButton, "所有螢幕關閉、只開一個虛擬螢幕給遠端連線軟體使用的模式");
            addButton.Margin = new Padding(0, 6, 3, 0);
            duplicateButton.Margin = new Padding(3, 6, 3, 0);
            removeButton.Margin = new Padding(3, 6, 0, 0);
            upButton.Margin = new Padding(0, 6, 3, 0);
            downButton.Margin = new Padding(3, 6, 3, 0);

            // ---- 右邊：選取的模式 ----
            name = new TextBox { Width = 200, Anchor = AnchorStyles.Left };
            name.TextChanged += delegate { OnNameChanged(); };
            nameError = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 3, 3, 0), Visible = false };

            colors = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
            foreach (int rgb in ModeConfig.Palette)
            {
                int c = rgb;
                Button swatch = Swatch(c);
                swatch.Click += delegate { SetColor(c); };
                colors.Controls.Add(swatch);
            }
            var otherColor = new Button { Text = "其他…", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            otherColor.Click += delegate { PickOtherColor(); };
            colors.Controls.Add(otherColor);

            manualAutoSwitch = Choice("維持", "開啟", "關閉");
            manualAutoSwitch.SelectedIndexChanged += delegate
            {
                if (Selected is ModeConfig m && !showing) { m.ManualAutoSwitch = (ManualAutoSwitch)manualAutoSwitch.SelectedIndex; MarkEdited(); }
            };
            unlisted = Choice("維持現狀", "關閉");
            unlisted.SelectedIndexChanged += delegate
            {
                if (Selected is ModeConfig m && !showing) { m.UnlistedMonitors = (UnlistedMonitors)unlisted.SelectedIndex; MarkEdited(); }
            };

            monitorsSummary = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            captureButton = new Button { Text = "以目前螢幕狀態覆蓋此模式", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };
            captureButton.Click += delegate { CaptureToSelectedMode(); };
            captureNote = new Label { AutoSize = true, ForeColor = Color.SeaGreen, Margin = new Padding(3, 3, 3, 0), Visible = false };
            captureBlocked = SmallHint(AppCoordinator.RemoteCaptureText);
            captureBlocked.ForeColor = Color.DarkOrange;

            // 潛行模式：虛擬螢幕的解析度與更新率（從驅動提供的模式中挑常見的）
            virtualResolution = Choice();
            virtualResolution.Width = 120;
            virtualResolution.SelectedIndexChanged += delegate { OnVirtualResolutionChanged(); };
            virtualRate = Choice();
            virtualRate.Width = 80;
            virtualRate.SelectedIndexChanged += delegate
            {
                if (!showing && Selected is { IsRemote: true } m && virtualRate.SelectedItem is Rate r)
                {
                    m.VirtualMode = m.VirtualMode with { RefreshRate = r.Hz };
                    MarkEdited();
                }
            };
            var virtualRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            virtualRow.Controls.Add(virtualResolution);
            virtualRow.Controls.Add(virtualRate);

            // 潛行模式：虛擬螢幕驅動不可用時的警告與「安裝驅動」
            driverWarning = new Label { AutoSize = true, ForeColor = Color.Firebrick, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 0) };
            installDriver = new Button { Text = "安裝驅動", AutoSize = true, Margin = new Padding(3, 3, 3, 0) };
            installDriver.Click += delegate { driver.Run(install: true); };
            driverDetail = SmallHint("");
            driver.Changed += ShowDriver;

            // 右欄可捲動；內層表格寬度跟著右欄（Dock = Top），不會出現水平捲軸
            detailsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Margin = new Padding(0),
            };
            detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Add(Control c, List<Control>? only = null)
            {
                detailsTable.Controls.Add(c);
                only?.Add(c);
            }
            Add(Section("名稱", first: true));
            Add(Wrap(name));
            Add(nameError);
            Add(Section("圖示顏色"));
            Add(colors);
            Add(Section("手動切換進入此模式時，自動切換要"));
            Add(Wrap(manualAutoSwitch));
            Add(SmallHint("例如手動切到省電時選「關閉」，就不會一動滑鼠又被切回來。"));

            Add(Section("未記錄的螢幕"), normalOnly);
            Add(Wrap(unlisted), normalOnly);
            Add(SmallHint("套用此模式時，接上但此模式沒有記錄的螢幕要維持現狀或關閉。"), normalOnly);
            Add(Section("螢幕設定"), normalOnly);
            Add(monitorsSummary, normalOnly);
            Add(captureButton, normalOnly);
            Add(captureNote, normalOnly);
            Add(captureBlocked, normalOnly);
            Add(SmallHint("擷取的結果按「套用」或「套用並關閉」後才存檔，存入的模式隨即成為目前模式。"), normalOnly);

            Add(Section("虛擬螢幕的解析度與更新率"), remoteOnly);
            Add(virtualRow, remoteOnly);
            Add(SmallHint("遠端期間 Chrome 遠端桌面等軟體調整了解析度，程式不會改回來，也不會存進模式。"), remoteOnly);
            Add(driverWarning, remoteOnly);
            Add(installDriver, remoteOnly);
            Add(driverDetail, remoteOnly);
            Add(Section("潛行模式"), remoteOnly);
            Add(SmallHint(
                "進入潛行模式時所有螢幕都關閉，電腦照常運作，只開一個虛擬螢幕給遠端連線軟體（例如 Chrome 遠端桌面）擷取，" +
                "遠端畫面不再被實體螢幕的數量、排列與解析度綁住。也能省下螢幕的電與部分顯示卡負擔（省電效果不及睡眠，它是為了同時支援遠端）。\n" +
                "「潛行」指螢幕全暗，不是防窺：坐在電腦前的人動一下實體鍵盤滑鼠，就依內建切換條件回到一般模式（目標在「切換條件」分頁選）。"), remoteOnly);

            details = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
            details.Controls.Add(detailsTable);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(details, 1, 0);
            Controls.Add(root);

            app.Changed += OnAppChanged;
        }

        /// <summary>在視窗中最後一次擷取存入的模式（套用時傳給協調者）；沒有擷取時為 null。</summary>
        public int? CapturedModeId { get; private set; }

        /// <summary>在視窗中擷取過、尚未存檔的所有模式（顯示「已擷取」標示用）。</summary>
        readonly HashSet<int> capturedModes = new();

        void MarkCaptured(int modeId)
        {
            CapturedModeId = modeId;
            capturedModes.Add(modeId);
        }

        ModeConfig? Selected => list.SelectedItem is int id ? draft.FindMode(id) : null;

        public override string HelpText =>
            "【模式】\n" +
            "・模式是一組有名稱的螢幕設定（從它出發的切換條件在「切換條件」分頁設定）。右鍵選單依這裡的順序列出所有模式，點一下即手動切換。\n" +
            "・用「新增」「複製」「刪除」管理模式，在右邊修改名稱、圖示顏色與下列設定。\n" +
            "・拖拉清單中的模式或按「上移」「下移」調整順序；套用後 cfg 的模式編號依新順序重新編排，切換條件跟著改。\n" +
            "・刪除模式時，來源模式或目標模式是它的切換條件會一併刪除（內建切換條件除外，見下方）；最後一個模式、最後一個一般模式與目前模式不能刪除。\n" +
            "・每台螢幕的位置、解析度等在「螢幕配置」分頁調整。\n\n" +
            "【潛行模式】\n" +
            "・「新增潛行模式（虛擬螢幕）」建立潛行模式，清單上標示「（潛行）」。複製潛行模式得到潛行模式；一般模式和潛行模式不能互相轉換。\n" +
            "・效果：所有螢幕關閉，電腦照常運作，只開一個虛擬螢幕給遠端連線軟體（例如 Chrome 遠端桌面）擷取。" +
            "遠端畫面不再被實體螢幕的數量、排列與解析度綁住，視窗也不會開在遠端不方便看的螢幕上。\n" +
            "・也能省下螢幕的電與部分顯示卡負擔，但省電效果不及睡眠：它是為了在省電的同時支援遠端。\n" +
            "・「潛行」指螢幕全暗，不是防窺：坐在電腦前的人動一下實體鍵盤滑鼠就會離開。\n" +
            "・虛擬螢幕的解析度與更新率在右邊選（預設 1920 × 1080、60 Hz）。遠端期間 Chrome 遠端桌面調整了解析度，程式不會改回來，也不會存進模式。\n" +
            "・潛行模式沒有「未記錄的螢幕」與螢幕配置，也不能擷取。\n" +
            "・需要虛擬螢幕驅動（在「一般設定」分頁安裝）。驅動不可用時，切換到潛行模式會被拒絕並跳出通知，維持原本的模式。\n" +
            "・每個潛行模式自帶一條內建切換條件：偵測到使用（實體鍵盤滑鼠）就切換到所選的一般模式，一律強制、不能刪除，" +
            "只能在「切換條件」分頁改目標模式。刪除潛行模式時一起刪除；它的目標模式被刪除時，改成順序第一個一般模式。\n" +
            "・離開潛行模式若不是經由切換（程式啟動時目前模式是潛行模式、虛擬螢幕意外消失），會回到復原模式：最後一個處於過的一般模式。\n\n" +
            "【套用模式時】\n" +
            "・模式中記錄但沒接上的螢幕會略過。\n" +
            "・接上但模式中沒有記錄的螢幕，依「未記錄的螢幕」維持現狀或關閉。\n" +
            "・套用後沒有任何螢幕會開著時拒絕切換；套用失敗時維持原本的模式，自動切換暫停 1 分鐘。\n" +
            "・手動切換進入模式時，自動切換依模式的設定維持、開啟或關閉。例如手動切到省電時選「關閉」，就不會一動滑鼠又被切回來。\n\n" +
            "【擷取】\n" +
            "・「以目前螢幕狀態覆蓋此模式」把目前每台螢幕的狀態存進選取的一般模式；「以目前螢幕狀態新增模式」存成一個新的一般模式。\n" +
            "・處於潛行模式時無法擷取（所有螢幕都關閉了，擷取會把「全部關閉」存進模式）。\n" +
            "・按「套用」或「套用並關閉」後才存檔，存入的模式隨即成為目前模式，並重新開始計時。";

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            // 依位置保留選取：套用後模式會依順序重新編號，原本的編號可能已指向別的模式
            int index = list.SelectedIndex;
            RefreshList(index >= 0 && index < d.Modes.Count ? d.Modes[index].Id : app.Config.CurrentModeId);
        }

        public override void OnApplied()
        {
            CapturedModeId = null;
            capturedModes.Clear();
            OnLoad(draft);   // 套用後模式依順序重新編號：清單中的編號要重建（依位置保留選取）
        }

        /// <summary>重建清單並選取 <paramref name="selectId"/>（不存在時選第一個）。</summary>
        void RefreshList(int selectId)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ModeConfig m in draft.Modes) list.Items.Add(m.Id);
            int index = list.Items.IndexOf(selectId);
            list.SelectedIndex = index >= 0 ? index : (list.Items.Count > 0 ? 0 : -1);
            list.EndUpdate();
            ShowSelected();
        }

        /// <summary>把選取的模式顯示到右邊的欄位。</summary>
        void ShowSelected()
        {
            ModeConfig? m = Selected;
            showing = true;
            try
            {
                details.Enabled = duplicateButton.Enabled = removeButton.Enabled = m != null;
                upButton.Enabled = list.SelectedIndex > 0;
                downButton.Enabled = list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count - 1;
                if (m == null) return;
                if (name.Text != m.Name) name.Text = m.Name;
                manualAutoSwitch.SelectedIndex = (int)m.ManualAutoSwitch;
                unlisted.SelectedIndex = (int)m.UnlistedMonitors;
                foreach (Control c in normalOnly) c.Visible = !m.IsRemote;
                foreach (Control c in remoteOnly) c.Visible = m.IsRemote;
                if (m.IsRemote) ShowVirtualMode(m.VirtualMode);
            }
            finally
            {
                showing = false;
            }
            UpdateNameError();
            UpdateColors();
            UpdateMonitors();
            UpdateCapture();
            ShowDriver();
        }

        // ---- 潛行模式：虛擬螢幕 ----

        /// <summary>
        /// 解析度與更新率下拉選單：常見的模式（<see cref="VirtualDisplayModes.Common"/>），模式記錄的值不在清單中時（手動編輯 cfg）也列出。
        /// </summary>
        void ShowVirtualMode(VideoMode v)
        {
            virtualResolution.Items.Clear();
            var resolutions = VirtualDisplayModes.Common.Select(c => new Resolution(c.Width, c.Height)).Distinct().ToList();
            var current = new Resolution(v.Width, v.Height);
            if (!resolutions.Contains(current)) resolutions.Add(current);
            foreach (Resolution r in resolutions.OrderByDescending(r => r.Width * r.Height)) virtualResolution.Items.Add(r);
            virtualResolution.SelectedItem = current;
            ShowVirtualRates(v);
        }

        void ShowVirtualRates(VideoMode v)
        {
            virtualRate.Items.Clear();
            foreach (int hz in VirtualRates(v.Width, v.Height, v.RefreshRate)) virtualRate.Items.Add(new Rate(hz));
            virtualRate.SelectedItem = new Rate(v.RefreshRate);
        }

        /// <summary>解析度 <paramref name="width"/> × <paramref name="height"/> 的常見更新率（由高到低）；<paramref name="recorded"/> 大於 0 且不在清單中時也列出。</summary>
        static List<int> VirtualRates(int width, int height, int recorded)
        {
            var list = VirtualDisplayModes.Common.Where(c => c.Width == width && c.Height == height).Select(c => c.RefreshRate).ToList();
            if (recorded > 0 && !list.Contains(recorded)) list.Add(recorded);
            return list.OrderByDescending(hz => hz).ToList();
        }

        void OnVirtualResolutionChanged()
        {
            if (showing || Selected is not { IsRemote: true } m || virtualResolution.SelectedItem is not Resolution r) return;
            // 更新率：新解析度也有目前的更新率就保留，否則用 60，再沒有就用最高的
            List<int> rates = VirtualRates(r.Width, r.Height, 0);
            int hz = rates.Contains(m.VirtualMode.RefreshRate) ? m.VirtualMode.RefreshRate : rates.Contains(60) ? 60 : rates.FirstOrDefault();
            m.VirtualMode = new VideoMode(r.Width, r.Height, hz > 0 ? hz : m.VirtualMode.RefreshRate);
            MarkEdited();
            showing = true;
            try
            {
                ShowVirtualRates(m.VirtualMode);
            }
            finally
            {
                showing = false;
            }
        }

        /// <summary>潛行模式選取中且驅動不可用（或正在安裝）時，顯示警告與「安裝驅動」。</summary>
        void ShowDriver()
        {
            if (IsDisposed) return;
            bool remote = Selected is { IsRemote: true };
            bool problem = driver.Running != null || (driver.Status is DriverStatus s && s.State != DriverState.Installed);
            driverWarning.Visible = installDriver.Visible = remote && problem;
            driverWarning.Text = driver.Running != null
                ? "虛擬螢幕驅動：" + driver.StateText
                : "虛擬螢幕驅動：" + driver.StateText + "，無法切換到潛行模式。";
            installDriver.Enabled = driver.CanInstall;
            driverDetail.Text = driver.DetailText;
            driverDetail.Visible = remote && problem && driver.DetailText.Length > 0;
        }

        /// <summary>擷取按鈕：潛行模式不能被覆蓋；處於潛行模式（或切換中）時都停用，並說明原因。</summary>
        void UpdateCapture()
        {
            string? blocked = app.CaptureBlocker;
            captureButton.Enabled = blocked == null && Selected is { IsRemote: false };
            captureNewButton.Enabled = blocked == null;
            captureBlocked.Visible = app.InRemoteMode && Selected is { IsRemote: false };
            string tip = app.InRemoteMode ? AppCoordinator.RemoteCaptureText : "";
            tips.SetToolTip(captureButton, tip);
            tips.SetToolTip(captureNewButton, tip);
        }

        void OnNameChanged()
        {
            if (showing || Selected is not ModeConfig m) return;
            m.Name = name.Text;
            MarkEdited();
            UpdateNameError();
            list.Invalidate();
        }

        void UpdateNameError()
        {
            ModeConfig? m = Selected;
            string? error = m == null ? null : draft.ValidateModeName(m.Id, m.Name);
            nameError.Text = error ?? "";
            nameError.Visible = error != null;
        }

        void SetColor(int rgb)
        {
            if (Selected is not ModeConfig m) return;
            m.Color = rgb;
            MarkEdited();
            UpdateColors();
            list.Invalidate();
        }

        void PickOtherColor()
        {
            if (Selected is not ModeConfig m) return;
            using var dialog = new ColorDialog
            {
                Color = ToColor(m.Color),
                FullOpen = true,
                CustomColors = ModeConfig.Palette.Select(ToBgr).ToArray(),
            };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                SetColor((dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B);
        }

        /// <summary>選取中的顏色方塊加粗外框。</summary>
        void UpdateColors()
        {
            int color = Selected?.Color ?? ModeConfig.NoColor;
            foreach (Control c in colors.Controls)
            {
                if (c is Button b && b.Tag is int rgb)
                {
                    bool on = rgb == color;
                    b.FlatAppearance.BorderSize = on ? 3 : 1;
                    b.FlatAppearance.BorderColor = on ? SystemColors.ControlText : SystemColors.ControlDark;
                }
            }
        }

        void UpdateMonitors()
        {
            ModeConfig? m = Selected;
            if (m == null) return;
            monitorsSummary.Text = m.Monitors.Count == 0
                ? "（沒有記錄任何螢幕）"
                : "記錄了 " + m.Monitors.Count + " 台螢幕：" + string.Join("、", m.Monitors.Select(Describe));
            captureNote.Text = "已擷取目前螢幕狀態（尚未存檔）。";
            captureNote.Visible = capturedModes.Contains(m.Id);
        }

        static string Describe(MonitorSettings s)
        {
            string label = s.Name.Length > 0 ? s.Name : s.Id.ToString();
            return label + (s.Enabled ? "（" + s.Width + "x" + s.Height + "）" : "（關閉）");
        }

        // ---- 新增、複製、刪除 ----

        void AddMode()
        {
            ModeConfig m = draft.AddMode();
            MarkEdited();
            RefreshList(m.Id);
            FocusName();
        }

        void AddRemoteMode()
        {
            ModeConfig m;
            try
            {
                m = draft.AddRemoteMode();
            }
            catch (InvalidOperationException e)
            {
                MessageBox.Show(FindForm(), "無法新增潛行模式：" + e.Message + "。", FindForm()?.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            MarkEdited();
            RefreshList(m.Id);
            FocusName();
        }

        void DuplicateMode()
        {
            if (Selected is not ModeConfig m) return;
            ModeConfig copy = draft.DuplicateMode(m.Id);
            MarkEdited();
            RefreshList(copy.Id);
            FocusName();
        }

        void RemoveMode()
        {
            if (Selected is not ModeConfig m) return;
            string label = DisplayName(m);

            // 目前模式讀協調者的即時值：設定視窗開著時仍可從工作列手動切換
            if (draft.ModeRemovalBlocker(m.Id, app.Config.CurrentModeId) is string reason)
            {
                MessageBox.Show(FindForm(), "無法刪除「" + label + "」：" + reason + "。", FindForm()?.Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<SwitchCondition> affected = draft.ConditionsReferencing(m.Id);
            List<SwitchCondition> retargeted = draft.BuiltInConditionsRetargetedBy(m.Id);
            string text = "要刪除" + (m.IsRemote ? "潛行模式" : "模式") + "「" + label + "」嗎？";
            if (affected.Count > 0)
                text += "\n\n以下切換條件的來源模式或目標模式是「" + label + "」，會一併刪除：\n" +
                        string.Concat(affected.Select(s => "・" + s.Describe(draft) + "\n"));
            if (retargeted.Count > 0)
            {
                string fallback = draft.Modes.FirstOrDefault(x => !x.IsRemote && x.Id != m.Id) is ModeConfig f ? DisplayName(f) : "";
                text += "\n\n以下內建切換條件的目標模式是「" + label + "」，會改成順序第一個一般模式「" + fallback + "」：\n" +
                        string.Concat(retargeted.Select(s => "・" + s.Describe(draft) + "\n"));
            }
            text += "\n按「套用」或「套用並關閉」後才會存檔。";
            if (MessageBox.Show(FindForm(), text, FindForm()?.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            int index = list.SelectedIndex;
            draft.RemoveMode(m.Id);
            if (CapturedModeId == m.Id) CapturedModeId = null;
            capturedModes.Remove(m.Id);
            MarkEdited();
            RefreshList(draft.Modes[Math.Min(index, draft.Modes.Count - 1)].Id);
        }

        // ---- 調整順序 ----

        void MoveSelected(int delta)
        {
            int from = list.SelectedIndex;
            int to = from + delta;
            if (from < 0 || to < 0 || to >= draft.Modes.Count) return;
            MoveMode(from, to);
        }

        /// <summary>把第 <paramref name="from"/> 個模式移到第 <paramref name="to"/> 個位置並選取它。編號在套用時才依新順序重新編排。</summary>
        void MoveMode(int from, int to)
        {
            int id = draft.Modes[from].Id;
            draft.MoveMode(from, to);
            MarkEdited();
            RefreshList(id);
        }

        void OnListMouseDown(object? sender, MouseEventArgs e)
        {
            dragFrom = e.Button == MouseButtons.Left ? list.IndexFromPoint(e.Location) : -1;
            dragStart = e.Location;
        }

        void OnListMouseMove(object? sender, MouseEventArgs e)
        {
            if (dragFrom < 0 || (e.Button & MouseButtons.Left) == 0) return;
            Size drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - dragStart.X) < drag.Width && Math.Abs(e.Y - dragStart.Y) < drag.Height) return;
            int from = dragFrom;
            dragFrom = -1;
            list.DoDragDrop(from, DragDropEffects.Move);
            SetInsertAt(-1);
        }

        void OnListDragOver(object? sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(int)))
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Move;
            SetInsertAt(InsertIndex(list.PointToClient(new Point(e.X, e.Y))));
        }

        void OnListDragDrop(object? sender, DragEventArgs e)
        {
            int at = insertAt;
            SetInsertAt(-1);
            if (e.Data.GetData(typeof(int)) is not int from || at < 0) return;
            int to = at > from ? at - 1 : at;   // 先移除自己，插入位置往前一格
            if (to != from) MoveMode(from, to);
        }

        /// <summary>游標位置對應的插入位置：在某個模式的上半部插在它之前，下半部插在它之後。</summary>
        int InsertIndex(Point p)
        {
            int index = list.IndexFromPoint(p);
            if (index < 0) return p.Y < 0 ? 0 : list.Items.Count;
            Rectangle r = list.GetItemRectangle(index);
            return p.Y > r.Top + r.Height / 2 ? index + 1 : index;
        }

        void SetInsertAt(int value)
        {
            if (insertAt == value) return;
            insertAt = value;
            list.Invalidate();
        }

        // ---- 擷取 ----

        void CaptureToSelectedMode()
        {
            if (Selected is not ModeConfig m) return;
            string label = DisplayName(m);
            DialogResult r = MessageBox.Show(FindForm(),
                "要以目前的螢幕狀態（每台螢幕的開關、座標、解析度、更新率、方向與主螢幕）覆蓋「" + label + "」的螢幕設定嗎？\n\n" +
                "目前沒接上的螢幕維持原本的設定。按「套用」或「套用並關閉」後才會存檔，「" + label + "」也會成為目前模式。",
                FindForm()?.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            List<MonitorSettings>? captured = app.CaptureForSettings(m.Monitors);   // 失敗時協調者會通知
            if (captured == null) return;
            m.Monitors.Clear();
            m.Monitors.AddRange(captured);
            MarkCaptured(m.Id);
            MarkEdited();
            ShowSelected();
        }

        void CaptureToNewMode()
        {
            List<MonitorSettings>? captured = app.CaptureForSettings(Array.Empty<MonitorSettings>());
            if (captured == null) return;
            ModeConfig m = draft.AddMode();
            m.Monitors.AddRange(captured);
            MarkCaptured(m.Id);
            MarkEdited();
            RefreshList(m.Id);
            FocusName();
        }

        void FocusName()
        {
            name.Focus();
            name.SelectAll();
        }

        // ---- 目前模式的即時標示 ----

        void OnAppChanged()
        {
            if (IsDisposed) return;
            list.Invalidate();   // 目前模式可能從工作列改變
            UpdateCapture();     // 進入或離開潛行模式
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                app.Changed -= OnAppChanged;
                driver.Changed -= ShowDriver;
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        void DrawModeItem(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || list.Items[e.Index] is not int id || draft.FindMode(id) is not ModeConfig m) return;

            int size = e.Bounds.Height - 6;
            var swatch = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 3, size, size);
            using (var brush = new SolidBrush(ToColor(m.Color))) e.Graphics.FillRectangle(brush, swatch);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, swatch);

            string text = DisplayName(m) + (m.IsRemote ? "（潛行）" : "") + (id == app.Config.CurrentModeId ? "（目前）" : "");
            var textBounds = new Rectangle(swatch.Right + 6, e.Bounds.Top, e.Bounds.Right - swatch.Right - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, textBounds, e.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();

            // 拖拉排序的插入線：插在這個模式之前畫在上緣，插在最後一個之後畫在最後一個的下緣
            bool before = insertAt == e.Index, after = insertAt == list.Items.Count && e.Index == list.Items.Count - 1;
            if (before || after)
            {
                int y = before ? e.Bounds.Top + 1 : e.Bounds.Bottom - 2;
                using var pen = new Pen(SystemColors.Highlight, 2);
                e.Graphics.DrawLine(pen, e.Bounds.Left, y, e.Bounds.Right, y);
            }
        }

        static Color ToColor(int rgb) =>
            rgb == ModeConfig.NoColor ? SystemColors.Control : Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        /// <summary>ColorDialog 的自訂色以 0x00BBGGRR 表示。</summary>
        static int ToBgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

        // ---- 版面 ----

        // 說明文字由表格限制在欄寬內自動換行（與一般設定分頁相同）。不要在 Resize 中設定標籤的 MaximumSize：
        // 設定視窗依 DPI 縮放的過程中會觸發 Resize，改 MaximumSize 會讓縮放重跑，邊距被放大好幾次。

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            list.ItemHeight = Font.Height + 8;
        }

        /// <summary>左欄的按鈕：填滿所在的格子。</summary>
        static Button ListButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
        };

        /// <summary>
        /// 以自動大小的面板包住固定寬度的欄位：表格建立時欄寬還很小，直接放進表格的欄位會被壓窄，之後也不會恢復。
        /// </summary>
        static Control Wrap(Control c)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            panel.Controls.Add(c);
            return panel;
        }

        /// <summary>灰色的說明文字（與上方欄位的間距較小）。</summary>
        static Label SmallHint(string text)
        {
            Label h = Hint(text);
            h.Margin = new Padding(3, 2, 3, 0);
            return h;
        }

        static Button Swatch(int rgb)
        {
            var b = new Button
            {
                Tag = rgb,
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = ToColor(rgb),
                Margin = new Padding(3, 2, 3, 2),
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.MouseOverBackColor = b.BackColor;
            b.FlatAppearance.MouseDownBackColor = b.BackColor;
            return b;
        }

        static ComboBox Choice(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            return c;
        }

        /// <summary>粗體的欄位標題；<paramref name="first"/> 為第一個時上方不留間距。</summary>
        Label Section(string text, bool first = false) => BoldLabel(text, new Padding(3, first ? 0 : 14, 3, 4));
    }
}
