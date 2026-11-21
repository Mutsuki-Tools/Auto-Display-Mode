using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>切換條件引擎：閒置、偵測到使用、使用來源、聲音持續秒數、條件順序、串連切換、鉤子需求。</summary>
    public class SwitchConditionTests
    {
        // ---- 閒置 ----

        [Fact]
        public void 閒置達指定分鐘後切換到目標模式()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            var state = Assert.Single(h.LastApplied);
            Assert.Equal((1280, 720), (state.Width, state.Height));   // 依目標模式的螢幕設定套用

            h.FinishSwitch();
            Assert.Equal(Save, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 閒置可以用小時為單位()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 2, unit: IdleUnit.Hours));

            Assert.Equal(2 * 60 * 60, h.RunUntilSwitch(3 * 60 * 60));
        }

        [Fact]
        public void 閒置期間有鍵盤或滑鼠輸入就重新計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));
            h.Run(4 * 60);
            h.Signals.UseKeyboardOrMouse();

            Assert.Equal(1 + 5 * 60, h.RunUntilSwitch(10 * 60));   // 輸入在下一次 tick 才被看到，之後再閒置滿 5 分鐘
        }

        [Fact]
        public void 閒置只選滑鼠或鍵盤時持續播放聲音不影響閒置計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.MouseOrKeyboard));
            h.Signals.AudioPeak = 0.5f;

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
        }

        [Fact]
        public void 閒置只選聲音時滑鼠或鍵盤輸入不影響閒置計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Audio));

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60, () => h.Signals.UseKeyboardOrMouse()));
        }

        [Fact]
        public void 閒置選兩個來源時要全部都沒有使用才算閒置()
        {
            var sound = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Both));
            sound.Signals.AudioPeak = 0.5f;
            Assert.Equal(-1, sound.RunUntilSwitch(20 * 60));

            var input = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Both));
            Assert.Equal(-1, input.RunUntilSwitch(20 * 60, () =>
            {
                if (input.Clock.NowMs % 60_000 == 0) input.Signals.UseKeyboardOrMouse();   // 每分鐘動一下
            }));
        }

        [Fact]
        public void 閒置選兩個來源時從最後一個來源停止使用起計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Both));
            h.Signals.AudioPeak = 0.5f;
            h.Run(2 * 60);
            h.Signals.AudioPeak = 0f;

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
        }

        [Fact]
        public void 通知音這類短暫聲音不會打斷閒置計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Both));

            // 每 30 秒響 2 秒：未達聲音持續秒數（3 秒）
            int seconds = h.RunUntilSwitch(10 * 60, () => h.Signals.AudioPeak = h.Clock.NowMs / 1000 % 30 < 2 ? 0.5f : 0f);

            Assert.Equal(5 * 60, seconds);
        }

        // ---- 偵測到使用 ----

        [Fact]
        public void 偵測到使用_滑鼠動作切換到目標模式()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.Run(10);   // 度過啟動緩衝期
            Assert.Empty(h.Monitors.Applied);

            h.Signals.MoveMouse();
            h.Run(1);

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
            h.FinishSwitch();
            Assert.Equal(Play, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 偵測到使用_鍵盤輸入也算滑鼠或鍵盤的使用()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.Run(10);

            h.Signals.PressKey();
            h.Run(1);

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 偵測到使用_只看鉤子不看Windows最後輸入時間()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.Run(10);

            h.Signals.UseKeyboardOrMouse();
            h.Run(5);

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 偵測到使用_只選滑鼠或鍵盤時聲音不算使用()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.MouseOrKeyboard));
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(30);

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 偵測到使用_只選聲音時滑鼠或鍵盤不算使用()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Audio));
            h.Run(10);

            h.Run(5, () => { h.Signals.MoveMouse(); h.Signals.PressKey(); });
            Assert.Empty(h.Monitors.Applied);

            h.Signals.AudioPeak = 0.5f;
            h.Run(4);
            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 偵測到使用_選兩個來源時任一個出現使用就成立()
        {
            var mouse = Modes(Save, Sw.Usage(Save, Play, Sw.Both));
            mouse.Run(10);
            mouse.Run(1, () => mouse.Signals.MoveMouse());
            Assert.Single(mouse.Monitors.Applied);

            var sound = Modes(Save, Sw.Usage(Save, Play, Sw.Both));
            sound.Run(10);
            sound.Signals.AudioPeak = 0.5f;
            sound.Run(4);
            Assert.Single(sound.Monitors.Applied);
        }

        // ---- 聲音持續秒數 ----

        [Fact]
        public void 聲音持續超過門檻秒數才算使用()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Audio));
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(3);
            Assert.Empty(h.Monitors.Applied);

            h.Run(1);
            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 聲音中斷就重新計算持續秒數()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Audio));
            h.Run(10);

            for (int i = 0; i < 5; i++)
            {
                h.Signals.AudioPeak = 0.5f;
                h.Run(3);
                h.Signals.AudioPeak = 0f;
                h.Run(1);
            }

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 聲音持續秒數依一般設定()
        {
            var h = Modes(Save, "AudioSeconds=10", Sw.Usage(Save, Play, Sw.Audio));
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(10);
            Assert.Empty(h.Monitors.Applied);

            h.Run(1);
            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 音量未超過門檻不算聲音()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Audio));
            h.Run(10);

            h.Signals.AudioPeak = 0.09f;
            h.Run(30);

            Assert.Empty(h.Monitors.Applied);
        }

        // ---- 條件順序 ----

        [Fact]
        public void 同一來源模式有多條條件同時成立時執行表格中較前面的()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 5), Sw.Idle(Play, Save, 5));

            h.RunUntilSwitch(10 * 60);

            Assert.Equal("全關", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 前面的條件不成立時檢查下一條()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 10), Sw.Idle(Play, Save, 5));

            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 只評估來源模式等於目前模式的條件()
        {
            var h = Modes(AllOff, Sw.Idle(Play, Save, 5), Sw.Usage(Save, Play, Sw.Both));

            Assert.Equal(-1, h.RunUntilSwitch(30 * 60, () => { h.Signals.AudioPeak = 0.5f; h.Signals.MoveMouse(); }));
            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 手動切換到某個模式後從該模式出發()
        {
            var h = Modes(AllOff, Sw.Idle(Play, Save, 5));

            h.App.ManualSwitch(Play);
            h.FinishSwitch();

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));   // 從切換完成起閒置滿 5 分鐘
            Assert.Equal("省電", h.Monitors.AppliedTargets[1]);
        }

        // ---- 串連切換與切換後計時歸零 ----

        [Fact]
        public void 串連切換_切換後從新的模式出發並重新計時()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5), Sw.Idle(Save, AllOff, 10));

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            h.FinishSwitch();

            Assert.Equal(10 * 60, h.RunUntilSwitch(20 * 60));   // 閒置計時在切換完成時歸零
            h.FinishSwitch();

            Assert.Equal(new[] { "省電", "全關" }, h.Monitors.AppliedTargets);
            Assert.Equal(AllOff, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 切換完成後聲音的閒置計時也歸零()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.MouseOrKeyboard), Sw.Idle(Save, AllOff, 3, Sw.Audio));
            h.Signals.AudioPeak = 0.5f;
            h.Run(60);
            h.Signals.AudioPeak = 0f;
            Assert.Equal(4 * 60, h.RunUntilSwitch(10 * 60));   // 只看滑鼠或鍵盤：聲音不影響
            h.FinishSwitch();

            // 聲音在切換前 4 分鐘就停了；若沒歸零，緩衝期一結束就會切換
            Assert.Equal(3 * 60, h.RunUntilSwitch(10 * 60));
        }

        [Fact]
        public void 自動切換時跳出的通知說明是哪個條件()
        {
            var idle = Modes(Play, Sw.Idle(Play, Save, 2, unit: IdleUnit.Hours));
            idle.RunUntilSwitch(3 * 60 * 60);
            idle.FinishSwitch();
            Assert.Contains(("閒置 2 小時，已自動切換到「省電」。", NotificationKind.Info), idle.Notifier.Shown);

            var usage = Modes(Save, Sw.Usage(Save, Play));
            usage.Run(10);
            usage.Run(1, () => usage.Signals.MoveMouse());
            usage.FinishSwitch();
            Assert.Contains(("偵測到使用，已自動切換到「遊玩」。", NotificationKind.Info), usage.Notifier.Shown);
        }

        // ---- 鉤子需求 ----

        [Fact]
        public void 目前模式只有閒置條件時不安裝任何鉤子()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Both), Sw.Usage(Save, Play));

            h.Run(60, () => h.Signals.MoveMouse());

            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 偵測到使用只選聲音時不安裝鉤子()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Audio));

            h.Run(1);

            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 進入有偵測到使用加滑鼠或鍵盤條件的模式時安裝鉤子_離開時移除()
        {
            var h = PlayAndSave(Play);
            h.Run(1);
            Assert.False(h.Signals.Policy.Install);

            h.RunUntilSwitch(10 * 60);                // 開始切換到「省電」
            Assert.False(h.Signals.Policy.Install);   // 切換期間不偵測
            h.FinishSwitch();
            Assert.True(h.Signals.Policy.Install);

            h.Run(10);
            h.Run(1, () => h.Signals.MoveMouse());    // 開始切換回「遊玩」
            Assert.False(h.Signals.Policy.Install);
            h.FinishSwitch();
            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 鉤子需求帶入滑鼠移動門檻與遠端輸入過濾設定()
        {
            var custom = Modes(Save, "MouseMoveThreshold=25", "IgnoreRemoteInput=0", Sw.Usage(Save, Play));
            custom.Run(1);
            Assert.Equal((true, 25, false),
                (custom.Signals.Policy.Install, custom.Signals.Policy.MoveThreshold, custom.Signals.Policy.IgnoreRemoteInput));

            var defaults = Modes(Save, Sw.Usage(Save, Play));
            defaults.Run(1);
            Assert.True(defaults.Signals.Policy.IgnoreRemoteInput);
        }

        [Fact]
        public void 忽略遠端輸入時遠端操作不會觸發偵測到使用()
        {
            var ignore = Modes(Save, Sw.Usage(Save, Play));
            ignore.Run(10);
            ignore.Run(5, () => ignore.Signals.RemoteInput());
            Assert.Empty(ignore.Monitors.Applied);

            var allow = Modes(Save, "IgnoreRemoteInput=0", Sw.Usage(Save, Play));
            allow.Run(10);
            allow.Run(1, () => allow.Signals.RemoteInput());
            Assert.Single(allow.Monitors.Applied);
        }
    }
}
