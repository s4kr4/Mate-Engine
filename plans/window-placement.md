# ウィンドウ位置とウィンドウサイズの保存・復元

## Context

起動するたびに、アバターのウィンドウがメインモニター中央・Normal サイズ（1536×1024）に戻る。

- **位置**: 保存する処理がどこにもない（`SaveLoadHandler.SettingsData` に項目がない）。
- **サイズ**: `SettingsHandlerButtons.CycleWindowSize` が `windowSizeState` を保存している。しかし起動時に復元する `RestoreWindowSize` は旧 `AvatarSettingsMenu` にしかなく、そのクラスは `Mate Engine Main` シーンに配置されていない。

分類: 中程度（VRMate の「Core に純粋ロジック、Runtime に Unity との接続、Bootstrap で生成」というパターンに沿う）。

## 方針

| ファイル | 種別 | 責任 |
| --- | --- | --- |
| `Assets/VRMate/Core/WindowPlacement.cs` | 新規 | 純粋ロジック。(1) 復元する位置の決定、(2) 保存するタイミングの判定 |
| `Assets/VRMate/Runtime/WindowPlacementKeeper.cs` | 新規 | `UniWindowController` と `SaveLoadHandler` を読み書きするだけ |
| `Assets/VRMate/Runtime/VRMateBootstrap.cs` | 変更 | `root.AddComponent<WindowPlacementKeeper>()` を追加 |
| `Assets/MATE ENGINE - Scripts/Settings/SaveLoadHandler.cs` | 変更 | `SettingsData` に `hasWindowPosition` / `windowPositionX` / `windowPositionY` を追加 |
| `Assets/VRMate/Tests/Editor/WindowPlacementTests.cs` | 新規 | Core の EditMode テスト |

- 保存先は `settings.json`。`--savefile` / `--datadir` による複数インスタンスの切り替えにそのまま従えるため。上流ファイルへの変更はフィールドの追加だけにする。
- 座標系: 位置もモニター矩形も `UniWindowController` の公開 API（`windowPosition` / `windowSize` / `GetMonitorCount()` / `GetMonitorRect(i)`）から取得する。どちらも LibUniWinC の同じ座標系で、WinAPI（左上原点）の値とは混ぜない。Core は座標系に依存しない計算だけを行う。

### Core: `WindowPlacement.cs`（`noEngineReferences`。float と独自の構造体を使う）

- `ScreenRect`（X, Y, Width, Height）
- `WindowPlacement.Restore(x, y, w, h, IReadOnlyList<ScreenRect> monitors)` → 復元する位置
  - ウィンドウの中心がいずれかのモニター（境界を含む閉区間）に乗っていれば、位置をそのまま返す。
  - 乗っていなければ、中心から距離が最も近いモニター（同じ距離なら番号が小さい方）を選び、中心がその内側に入るよう最小限だけ平行移動する。
  - ウィンドウ全体をモニター内に収めることはしない。透明なウィンドウを画面からはみ出させて置くのは普通の使い方で、収めると利用者が置いた場所からアバターが動くため。
  - x, y が非有限、またはモニターが（有効なものが）0枚のときは復元しない（起動時の位置のまま）。幅または高さが 0 以下の矩形は無視する。
- `PlacementSaver`（保存タイミングの判定）
  - `Begin(x, y)`: 復元が終わった位置を基準にする。`Begin` より前は常に保存しないと判定する。
  - `Tick(now, x, y, sizeMatches)` → bool: 位置が最後に保存した値と異なり、同じ位置のまま `SettleSeconds`（1秒）以上経ち、`sizeMatches` が真なら true。true を返したとき、その位置を保存済みとして記録する。
  - `ShouldSaveOnQuit(x, y, sizeMatches)`: 終了時の判定。待ち時間なしで同じ条件を見る。
  - 位置が落ち着いたかどうかで判定するので、ウィンドウを動かす5か所（ドラッグ・Hide・Locomotion・DanceSafetyZone・MoveToPrimary）に手を入れない。

### Runtime: `WindowPlacementKeeper`

1. `SaveLoadHandler.Instance` と `UniWindowController.current` がそろい、`windowSize` が正の値になるまで毎フレーム待つ。
2. `windowSizeState` に対応するサイズを `windowSize` に設定する。対応表は `SettingsHandlerButtons.CycleWindowSize` と同じ値（Normal 1536×1024、Big 2048×1536、Small 768×512）。範囲外の値は Normal として扱う。
3. 次のフレームで、`hasWindowPosition` が真なら `Restore` の結果を `windowPosition` に設定し、`saver.Begin(現在位置)` を呼ぶ。
4. 以降は毎フレーム `Tick(Time.unscaledTime, pos, sizeMatches)` を呼ぶ。true なら3フィールドを書き込み、`SaveToDisk()` を呼ぶ。`OnApplicationQuit` でも同じように保存する。
   - `sizeMatches` は「現在の `windowSize` が `windowSizeState` に対応するサイズと各軸 1px 以内で一致する」こと。大画面モード（`AvatarBigScreenHandler` がモニター全体に一時的に広げる）の間の位置は、これで保存されない。
   - 一度も一致しない場合は `WarnOnce` で一度だけ警告をログに出す。

## 想定入力範囲

| 項目 | 内容 |
| --- | --- |
| 信頼境界 | `settings.json` の値は信頼しない（手で編集されうる）。`UniWindowController` が返す位置・サイズ・モニター矩形は OS の値として信頼する |
| 想定する入力の軸 | 保存位置 x, y: 有限の float 全体（負の値、すべてのモニターの外側を含む）と非有限（NaN, ±Infinity）。`hasWindowPosition`: true / false / 項目なし（旧ファイル）。`windowSizeState`: 定義された3値と範囲外の整数（Normal として扱う）。モニター: 0〜8枚、負の座標を含み、隣接・重なり・離れた配置がありうる。幅または高さが 0 以下の矩形は無視する。ウィンドウサイズ: 正の値（0 以下なら復元を待つ） |
| 規模の上限 | モニター8枚。保存は位置が落ち着くたびに最大1回、1秒に1回を超えない |
| 実行環境の前提 | Windows スタンドアロン、Unity 6000.2.6f2。ロケールに依存しない |
| 想定しない条件 | 実行中のモニター構成の変更への追従（起動時だけ見る）。DPI の変更。Linux / macOS 版 |
| 判定の不変条件 | 復元後のウィンドウ中心は接続中のいずれかのモニター上にあり、保存位置がすでにそうなら位置は変えない。保存される位置は、ウィンドウが保存中のサイズ状態に対応する大きさだった時に観測された位置に限る |

## 完了条件

- Core のテストがすべて通る。対象は、中心がモニター上／外、境界上、離れたモニター、負の座標、等距離のとき番号の小さい方、非有限値、モニター0枚、幅0の矩形、そして `PlacementSaver` の判定（`Begin` 前、落ち着く前・後、`sizeMatches` が偽、同じ位置を二度保存しない、終了時の判定）。
- 既存の VRMate.Core.Tests が回帰しない。
- 実機（手動確認）: 移動とサイズ切り替えの後に再起動すると、同じ位置・同じサイズで表示される。保存したモニターを外して起動すると、残ったモニター上に表示される。

除外範囲: アバターのスケール（`avatarSize`）、旧 `AvatarSettingsMenu` の整理、`MoveToPrimaryScreen` の変更。
