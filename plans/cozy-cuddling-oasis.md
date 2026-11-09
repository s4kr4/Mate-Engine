# 計画: M10 アバター側のつなぎを Core の VoiceInputDisplay に移す（アプローチ差し戻し）

作成: 2026-11-09 / 状態: 承認済み（2026-11-09）・検証合格（2026-11-09、イテレーション 1） / 分類: 複雑（VRMate M10 の fork 側。計画の正本は `D:\s4kr4\workspace\VRMate\plans\2026-11-06-m10-voice-input.md` の方針 4・5、完了条件 4・5）

## Context

M10 のアバター側（listen を入力欄のプレースホルダーに、heard をユーザーの吹き出しに出す）は、Core の解釈（`BrainProtocol`・`BrainMessages`・`InputPlaceholder`）と `BrainLink` のイベントまでは検証に合格している。残っているのは ChatBot.cs（本家のサンプル、Assembly-CSharp でテストから参照できない）のつなぎで、次の判断を ChatBot 自身が持っている:

- BrainLink の 3 つのイベントを一度だけ購読する・OnDestroy で解除する
- heard 1 つにつき吹き出しを 1 つ、その文のまま足す
- プレースホルダーの文が変わったときだけ書く

これをソースの字句検査で守ろうとしたが、検証で 2 回続けて同じ根本原因で不合格になった（1 周目: パネルの開閉で打ち切る・Update から呼ばない・空のメソッドへつなぐ等 14 変異、2 周目: 別の打ち切りの述語・呼び出しの条件・解除と上書き等 15 変異以上が生存。同時に正しい書き方 7 通りを落とし、検査は +1259 行の解析器になった）。C# では同じ意味を任意の形で書けるので、書き方の列挙では許す側・禁じる側の両方が収束しない。ルールのアプローチ差し戻し（1 タスク 1 回）として、**判断を Core の参照できるクラスへ移し、振る舞いテストで守る**形に変える（philosophy: do-one-thing-well・independent-verification）。ChatBot にはそのクラスへ委ねる数行だけを残し、既存の `ReplyBubbleWriter`（`Assets/VRMate/Core/ReplyBubbleWriter.cs`）と `OnSpeechTextAppended_DelegatesToReplyBubbleWriter` と同じ「委ねていることだけをソースで見る」形にそろえる。

## 方針

### Core（新規 `Assets/VRMate/Core/VoiceInputDisplay.cs`、namespace `VRMate`）

```csharp
public interface IVoiceInputSource      // BrainLink が実装する（イベントはすでに同じ形で持っている）
{
    event Action<string> Heard;
    event Action<ListenState> ListenChanged;
    event Action Disconnected;
}

public interface IVoiceInputView        // ChatBot の入れ子クラスが実装する
{
    void AddPlayerBubble(string text);  // 文字で送ったときと同じユーザーの吹き出し
    void SetPlaceholder(string text);
}

public sealed class VoiceInputDisplay
{
    public VoiceInputDisplay(string connectedText, IVoiceInputView view); // view が null なら ArgumentNullException、connectedText の null は空文字
    public void Attach(IVoiceInputSource source); // 毎フレーム呼んでよい
    public void Detach();
    public void Tick(bool connected);             // 毎フレーム呼ぶ
}
```

- 中で `InputPlaceholder` を 1 つ持つ（既存のクラスとテストはそのまま使う）
- **Attach**: null なら何もしない（いまの購読を保つ）。同じ source なら何もしない（二重に購読しない）。別の source なら、前の source から 3 つとも解除し、状態を idle に戻して（切断と同じ）新しい方に 3 つ購読する
- **Detach**: いまの source から 3 つとも解除する。購読していなければ何もしない。Detach の後の Attach はまた一度だけ購読する
- **Heard**: `view.AddPlayerBubble(text)` をその文のまま 1 回呼ぶ（空・空白だけ・長い文・同じ文の繰り返しも、届いた数だけ）
- **ListenChanged / Disconnected**: `InputPlaceholder.OnListen` / `OnDisconnected` に渡すだけ（表示は次の Tick）
- **Tick(connected)**: `InputPlaceholder.Text(connected)` が前回 `SetPlaceholder` に渡した文と違うときだけ `SetPlaceholder` を呼ぶ（最初の Tick は必ず書く）
- メインスレッドから使う（スレッド安全にしない。`ReplyBubbleWriter` と同じ）

### Runtime（`Assets/VRMate/Runtime/BrainLink.cs`）

- `public class BrainLink : MonoBehaviour, IVoiceInputSource` にするだけ（イベントと購読者ごとに try/catch する `Raise` はいまのまま）

### ChatBot（`Assets/LLMUnity/Samples/ChatBot/ChatBot.cs`。本家で直すのはこれだけ）

- `brainLink`・`placeholder`・`placeholderText` フィールド、`SubscribeBrainLink()`、`OnBrainHeard` を消し、`VoiceInputDisplay voiceInput` フィールドと入れ子の `VoiceInputView : IVoiceInputView`（`AiBubbleView` と同じ形）に置き換える
  - `AddPlayerBubble(text)` → `owner.AddBubble(text, true)`、`SetPlaceholder(text)` → `owner.inputBubble.SetPlaceHolderText(text)`
- Start: `voiceInput = new VoiceInputDisplay(inputPlaceholder, new VoiceInputView(this));`
- Update と Start: `voiceInput.Attach(BrainLink.Instance);` と、`UpdateConnectionPlaceholder()` の中身を `voiceInput.Tick(BrainLink.Instance != null && BrainLink.Instance.IsConnected);` に
- OnDestroy: `voiceInput.Detach();`（OnDisable では解除しない。パネルを閉じても heard は吹き出しになる）

### テスト

- **新規 `Assets/VRMate/Tests/Editor/VoiceInputDisplayTests.cs`**（behavior。偽の source と記録する view で）: 下の完了条件 A1〜A10
- **`ChatBotReplyTextTests.cs`**: 前回 tester が足した 12 件と解析器（+1259 行）を取り除き、HEAD の 325 行に戻したうえで、既存の補助（`ClassBody`・`FindVoidMethod`・`Squash`）を使った薄い検査を足す（下の B1〜B4）。既存 2 件は変えない
- `BrainProtocolListenTests.cs`（57 件）・`InputPlaceholderTests.cs`（18 件）はそのまま

## 完了条件（テストの仕様文になる形）

VoiceInputDisplay（振る舞い）:
- A1. Attach した source が heard を送ると、その文のまま AddPlayerBubble が 1 回呼ばれ、続けて届いた heard は届いた順に 1 つずつ呼ばれる（空・空白だけ・8,000 文字・同じ文の繰り返しを含む）
- A2. 同じ source に何度 Attach しても、heard 1 つにつき AddPlayerBubble は 1 回だけ呼ばれる
- A3. Attach(null) は何もせず例外を出さず、すでに購読している source の heard はそのまま届く
- A4. Detach の後は、その source の heard・listen・切断は表示を変えず、Detach を何度呼んでも、購読前に呼んでも例外を出さない。Detach の後の Attach では heard 1 つにつき 1 回届く
- A5. 別の source に Attach すると、前の source の知らせは表示を変えず、新しい source の知らせだけが届き、listen の状態は idle に戻る
- A6. つながっていて recording を受けると次の Tick で「聞いています…」、recognizing で「聞き取っています…」、idle で渡された文を SetPlaceholder に書く
- A7. つながっていない Tick では、listen の状態に関わらず「頭脳につながっていません」を書く
- A8. 切断を受けると idle に戻り、つなぎ直した Tick では新しい listen が来るまで渡された文を書く
- A9. Tick は、前回書いた文と同じなら SetPlaceholder を呼ばず、最初の Tick と文が変わった Tick でだけ呼ぶ
- A10. view が null なら ArgumentNullException、connectedText が null なら接続中の idle は空文字

ChatBot（ソース検査。委ねていることだけを見る）:
- B1. Update は、本文の直下（条件の中でない文）で VoiceInputDisplay のフィールドの `Attach(BrainLink.Instance)` と `Tick(<IsConnected を読む式>)` を呼ぶ（`Tick` は Update から呼ぶ補助メソッド経由でもよい）
- B2. OnDestroy は VoiceInputDisplay のフィールドの `Detach()` を呼び、ChatBot のほかの場所では Detach を呼ばない
- B3. IVoiceInputView を実装する ChatBot の入れ子クラスで、AddPlayerBubble は受け取った文をそのまま `AddBubble(<文>, true)` に渡し、SetPlaceholder は受け取った文を `SetPlaceHolderText(<文>)` に渡し、どちらも `SendInput`・`TrySend` を呼ばない
- B4. ChatBot は BrainLink のイベント（Heard・ListenChanged・Disconnected）を自分で購読せず、「頭脳につながっていません」の文を自前で持たない

除外: Core の解釈・InputPlaceholder・BrainLink の Raise の作り直し（合格済み）、ChatBot のその他の挙動の変更

## 🛡️ 想定入力範囲

| 項目 | 内容 |
|---|---|
| 信頼境界 | 頭脳から届くフレームを信頼しない（Core の解釈で合格済み）。heard の text は空・空白だけ・8,000 文字超・制御文字・絵文字を含みうる。VoiceInputDisplay に渡る source は null・同じもの・別のものがありうる |
| 想定する入力の軸 | 呼び出しの順: {Attach・Detach・Tick・heard・listen・切断} の任意の並び（Attach の重複、Attach(null)、購読前の Detach、Detach の重複、別の source への Attach を含む）。heard の text: 任意の文字列（同じ文の繰り返し・空・長い文）。listen の状態: 3 つ、同じ状態の繰り返し・飛ばし。接続: Tick の connected が {true, false} の任意の並び |
| 規模の上限 | 1 フレームに振り分けるイベントは 16 件まで、受け取るメッセージは 8 MiB まで（既存） |
| 実行環境の前提 | Windows 11、Unity 6000.2.6f2、EditMode テスト。VoiceInputDisplay はメインスレッドからだけ使う |
| 想定しない条件 | (1) チャットパネル（`ChatMenuPanel`、シーンで `m_IsActive: 0`）を最初に開くまでに届いた heard と listen の取りこぼし（ChatBot の Start・Update が動かないため。AI の返事も同じ既存の挙動。ユーザー承認済み 2026-11-07）。(2) ChatBot.cs の、委ねる呼び出し以外の書き換え（Update の先頭への早期 return の追加など）。ChatBot はテストから参照できず、ソース検査は「委ねていること」だけを見る（既存の ReplyBubbleWriter のつなぎと同じ扱い）。判断の正しさは VoiceInputDisplay の振る舞いテストで守る。(3) BrainLink の振り分け（`Raise`）の自動テスト（既存の say・cue の振り分けと同じくテストなし。読んで確認済み） |
| 判定の不変条件 | VoiceInputDisplay は、Attach された source にだけ 3 つのイベントを 1 つずつ購読し、受け取った heard 1 つにつき AddPlayerBubble をその文のまま 1 回呼び、Tick のたびに InputPlaceholder.Text(connected) が前回書いた文と違うときだけ SetPlaceholder を呼ぶ |

## 進め方

| 手順 | 担当 | 中身 |
|---|---|---|
| 2a | `@tester` | `VoiceInputDisplayTests.cs` を新規（A1〜A10、behavior RED＝型が無いことによるコンパイルエラー）。`ChatBotReplyTextTests.cs` を HEAD に戻して B1〜B4 を足す（いまの ChatBot は委ねていないので RED）。RED がコンパイルエラーのときは、シグネチャのスタブで本体までコンパイルして他の原因が無いことを確かめる |
| 2b | `@implementer` | `VoiceInputDisplay.cs`（＋ .meta）、`BrainLink` に `IVoiceInputSource`、ChatBot を委ねる形に |
| 3 | `@code-safety-inspector` | イテレーション 1 から数え直す。変異試験（VoiceInputDisplay の購読の一度だけ・解除・Tick の変化検出・heard の加工、ChatBot の委ねる呼び出しの欠落） |
| 確かめ | ユーザー | ビルドしたアプリで完了条件 4・5（下の「確かめ方」） |

Unity の注意: batchmode のたびに `Assets/AddressableAssetsData/link.xml` と `.meta` が消えるので、その 2 ファイルだけ戻す。同じプロジェクトを 2 つの Unity で同時に開けないので、テストを流すエージェントは 1 つずつにする。`MateEngine.exe` と頭脳は止めない。

## 重要なファイル

- 新規: `Assets/VRMate/Core/VoiceInputDisplay.cs`、`Assets/VRMate/Tests/Editor/VoiceInputDisplayTests.cs`（＋ .meta）
- 変更: `Assets/VRMate/Runtime/BrainLink.cs`（インターフェースを足すだけ）、`Assets/LLMUnity/Samples/ChatBot/ChatBot.cs`、`Assets/VRMate/Tests/Editor/ChatBotReplyTextTests.cs`
- そのまま使う: `Assets/VRMate/Core/InputPlaceholder.cs`、`BrainProtocol.cs`・`BrainMessages.cs`、`ReplyBubbleWriter.cs`（形の手本）

## 確かめ方

- 自動: `Unity.exe -batchmode -projectPath D:/s4kr4/workspace/Mate-Engine -runTests -testPlatform EditMode -assemblyNames VRMate.Core.Tests ...` で全件通過・コンパイルエラー 0 件
- アプリ（ビルドが要る。`Build\MateEngine.exe` を閉じてから batchmode でビルド）: チャットパネルを開いた状態で、キーを押している間は入力欄に「聞いています…」、離すと「聞き取っています…」、終わると元の文に戻る。聞き取った文がユーザーの吹き出しとして返事より前に出る。パネルを閉じて話してから開いても吹き出しが出ている。頭脳を止めると入力欄が「頭脳につながっていません」になり、つなぎ直すと元に戻る
