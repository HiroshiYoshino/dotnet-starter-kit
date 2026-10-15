# Identity・Multitenancy・Auditing データ書き込み品質の修正計画

作成日: 2026-10-15

本書は、[調査報告書・解説版](data-write-quality-investigation-report.md)と[調査結果・詳細版](data-write-quality-investigation-results.md)を踏まえ、Identity、Multitenancy、Auditingの機能を修正するための計画です。ユーザー指定の「Audit」は、このリポジトリの`Auditing`モジュールとして扱います。

**この作業では計画書のみを作成します。アプリケーション、DB、共有基盤、Skillsは変更しません。** 本書の方式候補、API・設定変更、共有基盤の変更は承認済みではありません。調査で未確認だった保証を、計画の記載だけで確認済みとは扱いません。

## 目次

1. [修正の目標と対象範囲](#scope)
2. [実装前に決める条件](#decisions)
3. [課題の類型と修正作業の対応](#patterns)
4. [推奨する実施順と依存関係](#sequence)
5. [Identityの修正内容](#identity)
6. [Multitenancyの修正内容](#multitenancy)
7. [Auditingの修正内容](#auditing)
8. [共有基盤の扱いと承認境界](#shared)
9. [受入試験と完了条件](#acceptance)
10. [既存データ・移行・段階導入](#rollout)
11. [変更の分割と最終成果物](#deliverables)
12. [根拠と調査項目の対応](#references)

次に着手する変更の具体例は、[二要素認証の保存失敗と部分成功を直す案](#next-fix-example)に示します。Outboxの一本化は別検討として扱い、この変更の前提にはしません。

<a id="scope"></a>

## 1. 修正の目標と対象範囲

### 目指す状態

単に例外を減らすのではなく、対象機能について次の状態を作ります。

| 目標 | 完成時に説明できること |
|---|---|
| 成功と保存状態の一致 | 保存が失敗したのに成功を返さない。必要な更新がそろって初めて成功とする。 |
| 同時実行と再送への耐性 | 異なる操作は取りこぼさず、同じ操作は効果を増やさない。集合全体の条件も保つ。 |
| 必須処理の起動・再開 | 途中停止しても未完了を見つけ、同じ処理として再開するか、明示的な要対応状態へ進める。 |
| 外部効果と派生状態の回復 | DB、画像、キャッシュなどが食い違った場合、対象情報を失わずに照合・補修できる。 |
| 監査保証の明示 | 診断用の記録と、欠損を許容しない証跡を混同せず、合意した保証・保持・再投入を実装する。 |

### 対象・条件付き対象・対象外

| 区分 | 内容 |
|---|---|
| Identityの主対象 | 登録、パスワード、ロール・権限、token/session、二要素認証。 |
| Identityの改善対象 | プロフィール・画像、代理操作権限のキャッシュ。グループ編集は仕様判断後に必要部分を修正。 |
| Identityの維持対象 | ユーザー無効化・削除の既存ガードと保存結果確認。監査との接続を確認するが、目的のない全面改修はしない。 |
| Multitenancyの主対象 | 初期構築、期限延長、期限通知、テーマ画像、専用DBでのイベント受信先。 |
| Auditingの主対象 | 部分保存後も進めるSQL保存、失敗分類、照会・再投入、監査の意味、保持削除。 |
| 条件付きの大きな変更 | 監査の耐久受理、共通イベント受信契約の変更、新しい処理・補修記録、APIの操作ID導入。必要保証と変更範囲を承認してから実施。 |
| 追加調査から始める範囲 | テナント有効・無効切り替え、Identity内の未調査の派生操作、初期構築の各手順の再実行性。既知の不具合として一律修正しない。 |
| 原則対象外 | Billing、Catalog、Chat、Files、Webhooks、Notifications自体の機能修正、SSO追加、UI刷新、汎用ワークフロー基盤の新設。 |

他モジュールのContractsや共通基盤が必要な保証を提供しない場合は、依存作業として明示します。例えば、Identityの画像URL更新の順序問題はFiles側のイベント契約にも関係します。三モジュールだけの変更で解決できない場合に、Filesを無断で改修したり、解決済みと扱ったりはしません。

### 基準点

調査と本計画のコード基準は`a967537526f3b769c7d268da3ed2a6920af65ce9`です。計画作成時のHEADも同じです。利用者による依存更新や他の計画文書は保持します。

既存の重点Framework.Testsは依存更新後に46件成功していますが、本計画の受入試験ではありません。今回、新しい業務障害試験は実行していません。実装開始時にはHEAD・未コミット差分・実効設定を取り直し、変わった処理だけ再調査します。

<a id="decisions"></a>

## 2. 実装前に決める条件

計画全体を止めるのではなく、該当する作業の開始条件として解消します。推奨案は意思決定の出発点で、確定仕様ではありません。

| 決定事項 | 推奨する考え方 | この決定を待つ作業 |
|---|---|---|
| 対応するDB・テナント構成 | まず調査基準のPostgreSQLで保証を検証する。共有DB・専用DBの両方を維持するなら、両構成を受入条件にする。MSSQL等へ無検証で保証を広げない。 | 接続・ロック・migration・イベント受信の設計。 |
| 登録成功の範囲 | ユーザー、必須の初期権限、必須後続作業の起動記録をそろえる。メール到達とは区別する。 | 登録の保存境界と補修。 |
| パスワードポリシー | 変更・リセットに適用する履歴・期限条件を明示し、必要な保存を成功条件に含める。 | 変更・リセットの一体化。 |
| ロール・グループの競合仕様 | 管理者最低人数は必ず保護する。通常の集合編集で後勝ちを許すか、古い版を拒否するかは別に決める。 | 集合競合制御、グループの追加修正。 |
| refresh競合・応答喪失時の契約 | 同じ旧トークンから複数の有効な更新結果を作らない。敗者を拒否するか、安全に同じ結果へ収束させるかを決める。セッションなしを許す互換仕様も判断する。 | token/sessionの更新。 |
| 再送の識別と保持期間 | 期限延長などの加算には論理操作IDを使い、同じID・異なる入力は拒否する。保持期間と古いクライアントへの移行策を決める。 | 期限延長、必要なら登録・補修API。 |
| 初期構築の再開と新規やり直し | 同じ記録の再開と、新しい処理を開始する操作を区別する。inline実行の許可条件も明示する。 | 初期構築service・job・Retry契約。 |
| 監査の保証レベル | イベント種別ごとに診断用・必須証跡を分類する。必須証跡では「何を受理した時点から、どの障害まで失わないか」を決める。 | 耐久受理、業務処理との結合、失敗時の応答方針。 |
| 監査失敗と業務応答 | 必須証跡を保存できない場合に業務を拒否するか、耐久的な保留で受理するかを決める。業務commit後に失敗応答を返すだけの設計にはしない。 | 権限変更・認証操作と監査の接続。 |
| 保持・復旧の運用契約 | 画像清掃、未起動、キャッシュ反映、監査退避の検出・対応期限、担当、再投入権限を決める。監査の保持日数・削除保留・backup復元も含める。 | 各バックグラウンド処理と本番切替。 |

未決の間でも、保存結果の検査、登録の二重発行の解消、既存保証内での監査重複受理の修正などは、影響と互換性を確認して先行できます。一方、「必達か未決なので任意通知にする」「記録がないので成功したことにする」という実装はしません。

<a id="patterns"></a>

## 3. 課題の類型と修正作業の対応

| 修正パターン | 対象での適用先 | 共通化できる判断／個別に残る実装 |
|---|---|---|
| 保存結果・例外を正しく扱う | パスワード期限、ロール、二要素認証、Inbox、監査保存。 | 成功を偽らない原則は共通。IdentityResult、DB重複、外部障害の判定は個別。 |
| 複数保存の境界をそろえる | 登録、パスワード、権限、token/session、期限通知。 | 同じ接続のtransaction参加は再利用。UserManager内部保存、catalog store、Outboxの実際の接続は機能ごとに確認。 |
| 数値・集合の競合を制御する | 期限延長、管理者最低人数、グループ集合。 | 条件付き更新・ロック等の考え方は共通。保護対象はテナント、管理者集合、グループで異なる。 |
| 同じ操作の一回効果を保つ | 登録イベント、期限延長、初期構築、監査の再投入。 | 安定した操作識別を用いる。業務操作ID、元イベントID、監査記録IDを目的に応じて使い分ける。 |
| 起動と再開を耐久化する | 登録の必須作業、期限通知、初期構築、必要な画像清掃。 | 受理記録・未完了発見・有限retry・終端失敗の形をそろえる。無条件に同じ汎用テーブルへ集約しない。 |
| 外部効果と派生状態を収束させる | プロフィール・テーマ画像、権限cache、代理操作権限cache。 | 正本確定後の反映と補修を設計。画像削除とcache無効化は別の成功判定を持つ。 |
| 作用先を確定してから処理を構築する | 専用DBのイベント受信、監査のtenant別保存。 | 完全なtenant情報を先に解決。catalog、tenant DB、横断データの用途を混同しない。 |
| 監査の意味・耐久性・保持を分離する | 変更試行、commit済み変更、security記録、DLQ、retention。 | 種別と保証の契約は横断。保存方式・既存データの扱い・業務拒否の条件は別途設計。 |

トランザクションの追加だけでは、期限の二重延長や外部画像削除まで解決しません。リトライ回数を増やすだけでは、初期構築の起動漏れや監査の既存ID衝突を直せません。各パターンを組み合わせて、機能単位の完成条件を満たします。

<a id="sequence"></a>

## 4. 推奨する実施順と依存関係

### 実施順の全体像

| 段階 | 実施する作業 | 終了条件 |
|---|---|---|
| 準備 | 対象設定・既存テストを確認し、第2章の該当要件を決める。失敗点・並行実行の再現用fixtureを用意する。 | 必要保証と既存挙動を区別し、修正前に失敗する焦点の絞られた試験がある。 |
| 第1段階: 独立した先行修正 | 保存結果無視の是正、登録イベントの一本化、テーマ画像の削除順序と追跡、監査SQL保存の重複受理。 | 成功を偽る経路と明確な重複・不可逆欠落を減らす。各変更は単独で検証する。 |
| 第2段階: 作用先と保存境界 | 専用DBの受信先、必要なら承認済みの共通契約修正。Identityの登録・password・権限・token、期限通知の保存境界を整える。 | 実接続でrollback・commitを確認し、必要な業務状態と起動記録がそろう。 |
| 第3段階: 競合・再送・再開 | 管理者集合、token rotation、期限延長、初期構築の未起動回復と手順再開。 | 別接続・複数実行・process再起動で、取りこぼし・二重反映・永久待機を防ぐ。 |
| 第4段階: 派生状態・監査契約 | プロフィール画像、グループ・権限cache、代理操作権限cache。合意した監査保証、再投入、保持削除。 | 正本との照合・補修、監査の受理と成功の意味、終端失敗の運用が成立する。 |
| 導入 | migration、既存不整合の扱い、段階リリース、監視・復旧手順、利用者向けdocsをそろえる。 | 機能ごとの受入条件を満たし、残る保証外を公開した状態で切り替えられる。 |

これは推奨順で、全作業を一列に待たせるものではありません。例えば、保存結果無視の修正、期限計算の競合試験、監査SQL保存の改善は、それぞれ独立して進められます。監査を欠損不可とするなら、その耐久受理と業務側の連携は第2段階へ引き上げます。

### 実際に守る依存関係

| 後続作業 | 先に必要なこと |
|---|---|
| 登録の業務保存＋Outboxの一体化 | 登録成功条件と、統合イベント発行元の一本化。 |
| 専用DBの受信効果・Inboxの試験 | 完全なtenant metadataの解決と、consumer・Inbox構築前のscope設定。 |
| 管理者最低人数の完成判定 | 同期保存の一体性に加え、管理者集合全体の競合制御。個別userのstampだけでは完了にしない。 |
| token rotationの完成判定 | 旧tokenを条件とした一回の遷移、session保存境界、競合敗者と応答喪失の仕様。 |
| 期限延長の重複防止 | 操作ID・入力同一性・保持期間の契約。さらに異なる操作の競合制御も必要。 |
| 初期構築の自動回復 | 起動要求の耐久保存、同一実行の識別、各手順と秘密入力の再開方針。 |
| 必須監査を伴う業務操作の切替 | 監査の耐久受理・障害時応答・commit成否の契約と、必要な保存境界。 |
| migration・API変更の本番切替 | 互換期間、既存レコードの検査、再投入・rollback手順、docsとchangelog。 |

専用DBの共通修正が承認待ちでも、共有DBで完結する同期処理を進めることはできます。ただし、専用DB対応の受入条件を未達のまま、両構成に対応済みとはしません。

Outbox一本化の次に独立して着手する候補としては、**二要素認証の保存結果確認と、無効化・キー再生成の一体化**を推奨します。具体的な変更範囲とコード例は第5章に記載します。これは三モジュール全体の影響順位を変更するものではなく、要件決定待ちや共有基盤変更の少ない、小さく完結させやすい先行候補の選択です。

<a id="identity"></a>

## 5. Identityの修正内容

### 登録: 二重発行を先に解消し、登録全体の完了を定義する

**修正方針:** 統合イベントは`UserRegistrationService`の明示的なOutbox追加へ一本化します。`UserRegisteredEventHandler`から同義の統合イベントを作る経路を除去する際も、登録ドメインイベントに依存する他の処理は切り離して維持します。

次に、必須のuser・role/groupと起動記録を同じDB上で確定します。`UserManager`や関連serviceが内部で保存する点、Outboxが独立したcontextを持つ点を前提に、同じ接続への参加を実測します。外部メール送信やHangfire実行をtransaction内へ入れて解決しようとはしません。必要な送信・起動作業を耐久的に記録する境界を設計します。

**再実行と既存データ:** 新規登録の再送と、作成済みユーザーの不足補修を分けます。メールアドレスが一致するだけで第三者が補修・権限付与を起動できる設計にはせず、既存認証・確認トークンまたは認可された管理操作を使います。必須作業の欠落を検出できる状態を持たせます。

**受入条件:** 通常登録・外部認証登録で一つの論理イベントとなる。各保存間で停止しても全部戻るか、権限を検査した上で同じ登録の未完了部分を再開できる。ジョブ・Outboxへの二重追加を試験する。

### パスワード・二要素認証: 成功判定と保存境界をそろえる

**修正方針:** `UserPasswordService`、期限・履歴service、二要素認証の各handlerで、すべての`IdentityResult`を確認します。repository標準の例外・ローカライズに従って保存失敗を表面化し、成功の形で返しません。

パスワード本体・必要な期限・履歴、二要素認証の無効化・キー再生成は、それぞれ同じ業務操作の保存境界を設計します。リセットと通常変更で共有すべきポリシーを先に決め、単に同じメソッドを呼ぶことで意図しない仕様変更を起こさないようにします。パスワード履歴削除のような後処理を分離するなら、必要な保持制約を破らないことと補修方法を明示します。

**受入条件:** managerが保存失敗を返すfixtureで成功応答にならない。複数保存の各地点の失敗で、パスワードだけ変わる・認証キーだけ不整合になる状態を防ぐか検出・補修できる。補修のために旧パスワードを保存・再要求しない。

<a id="next-fix-example"></a>

### 具体例: 次の先行候補として二要素認証を修正する

#### この候補を選ぶ理由と範囲

有効化確定と無効化は、認証設定を変える重要な操作でありながら、現在は保存の`IdentityResult`を確認せず`true`を返します。無効化にはさらに二回の保存があります。問題と完成条件が具体的で、登録の発行方式、履歴ポリシー、監査の耐久保証といった別の意思決定から切り離して改善できます。

**この変更では新しいOutbox、ジョブ、API、汎用のtransaction serviceを追加しません。** 現行の二つのhandlerとIdentityの既存DbContextを使い、認証・現在パスワード・確認コードの検査と、成功時の`bool`応答は維持します。以前は成功して見えていた保存失敗をエラーとして返す点は、意図した挙動変更です。

| 変更先 | 現状 | 修正後のイメージ |
|---|---|---|
| `VerifyEnrollTwoFactorCommandHandler` | コード検証後、設定保存の結果を無視して成功。 | 保存結果を検査し、失敗時は標準例外へ。成功時だけ`true`。 |
| `DisableTwoFactorCommandHandler` | 無効化保存とキー再生成保存を順に行い、両結果を無視。 | 同じIdentityDbContextのtransaction内で両結果を検査し、両方成功した場合だけcommit。 |
| Identityのresource catalog | この修正用のメッセージは未追加。 | 有効化失敗・無効化失敗のメッセージを追加し、ProblemDetails境界で翻訳。 |
| Identity.Tests・Integration.Tests | この計画のための回帰試験は未追加。 | 失敗結果、後段失敗、rollback、既存ガードを対象に追加。 |

#### 現在の問題が分かるコード

以下は既存handlerの末尾です。有効化は一回、無効化は二回の保存を呼びますが、いずれも戻り値を判定していません。

```csharp
// VerifyEnrollTwoFactorCommandHandler
await _userManager.SetTwoFactorEnabledAsync(user, true);
return true;

// DisableTwoFactorCommandHandler
await _userManager.SetTwoFactorEnabledAsync(user, false);
await _userManager.ResetAuthenticatorKeyAsync(user);
return true;
```

根拠: [有効化確定handler][twofactor-enable]、[無効化handler][twofactor-disable]。

#### 有効化の修正イメージ

確認コードの検査までは維持し、その後の保存部分を次のように変えます。コードは**計画上の抜粋**で、必要な`using`・resource追加・試験はまだ実施していません。

```csharp
IdentityResult result = await _userManager
    .SetTwoFactorEnabledAsync(user, true)
    .ConfigureAwait(false);

if (!result.Succeeded)
{
    throw new CustomException(
        "Could not enable two-factor authentication.",
        result.Errors.Select(error => error.Description).ToList(),
        System.Net.HttpStatusCode.BadRequest)
    {
        MessageKey = "Identity.TwoFactorEnableFailed",
        ResourceSource = typeof(IdentityResources),
    };
}

return true;
```

`CustomException`にエラー一覧を渡す形は、既存の`UserStatusService`の保存結果確認に合わせます。新しい例外クラスや共通helperを作ることは前提にしません。`MessageKey`は新規追加案であり、既存キーとして使えるという意味ではありません。英語messageとresource metadataを付け、throw時には翻訳しない現在のローカライズ規約に従います。

この例のHTTP 400は既存のIdentityResult失敗処理に合わせた案です。競合を409に分ける場合は、実際のerror codeに基づく分類とAPI契約を別途そろえます。DB接続障害などの例外まで、すべて400や「コード不正」へ変換しません。

#### 無効化の修正イメージ

まずconstructorへ`IdentityDbContext`を追加して`_dbContext`へ保持します。既存の登録は`AddEntityFrameworkStores<IdentityDbContext>()`なので、UserManagerのstoreが同じDI scopeのcontextを利用する前提を確認し、統合試験でも検証します。独立した別contextを生成してtransactionを開始するだけでは意味がありません。

現在パスワードの確認を通過した後の保存部分は、次の形を候補にします。

```csharp
await using var transaction = await _dbContext.Database
    .BeginTransactionAsync(cancellationToken)
    .ConfigureAwait(false);

IdentityResult disableResult = await _userManager
    .SetTwoFactorEnabledAsync(user, false)
    .ConfigureAwait(false);

if (!disableResult.Succeeded)
{
    throw new CustomException(
        "Could not disable two-factor authentication.",
        disableResult.Errors.Select(error => error.Description).ToList(),
        System.Net.HttpStatusCode.BadRequest)
    {
        MessageKey = "Identity.TwoFactorDisableFailed",
        ResourceSource = typeof(IdentityResources),
    };
}

IdentityResult resetResult = await _userManager
    .ResetAuthenticatorKeyAsync(user)
    .ConfigureAwait(false);

if (!resetResult.Succeeded)
{
    throw new CustomException(
        "Could not disable two-factor authentication.",
        resetResult.Errors.Select(error => error.Description).ToList(),
        System.Net.HttpStatusCode.BadRequest)
    {
        MessageKey = "Identity.TwoFactorDisableFailed",
        ResourceSource = typeof(IdentityResources),
    };
}

await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
return true;
```

これにより、一回目の保存後にキー再生成が失敗しても、commitせずtransactionを破棄することでDB更新をrollbackする構成にします。`IdentityResult.Failed`と例外の両方を試験し、失敗後に同じtracked userを使って処理を続けません。DBが戻っても、メモリ上のuserの変更が自動で戻るとは限らないためです。

UserManagerのこれらのAPIにはCancellationToken引数がないため、架空のtoken引数は追加しません。transaction開始・commitなど対応するEF APIには伝播し、キャンセルを成功や別の保存エラーとして握りつぶしません。また、実装時に触る二handler内のawaitは既存規約に従って`ConfigureAwait(false)`を付けます。

このtransactionは**無効化とキー保存の部分成功を防ぐため**のもので、要求の重複排除、応答喪失時の同じ結果の再提示、監査の耐久性まで解決するものではありません。外側にtransactionがある呼出経路やproviderの実行再試行を有効にした構成では、transactionの所有・参加方法も確認し、無条件の入れ子transactionにしません。

#### 小さな変更でも、二つの完成条件を区別する

| 段階 | 実装すること | ここで直ること／残ること |
|---|---|---|
| 保存結果の確認 | 有効化・無効化・キー再生成の全結果を検査し、エラーを返す。 | 「保存失敗でも成功」は直る。ただし二回目の失敗で無効化だけ残る問題は、これだけでは直らない。 |
| 無効化の一体化 | 同じcontextのtransactionで二保存をまとめ、後段失敗時にrollbackする。 | 無効化・キー再生成の部分成功も防ぐ。今回の先行候補はこの段階までを完成範囲とする。 |

レビュー上二つに分ける場合でも、結果確認だけで「二要素認証の整合性対応完了」としません。migrationや共有基盤変更は、この案では予定しません。同一contextやtransaction参加の前提を満たせない場合は、その時点で計画を見直します。

#### 追加する試験の具体像

handlerの単体試験は`src\Tests\Identity.Tests\Handlers`の既存構成に合わせ、有効化・無効化の試験を追加する案です。UserManagerの代替には既存service試験のNSubstituteの構成を参考にします。transactionの本当の効果はmockだけでは証明しないため、無効化のrollbackはPostgreSQLの統合試験へ分けます。

| 条件 | 観測する結果 |
|---|---|
| 有効な確認コード、保存成功 | 有効化して`true`を返す。 |
| 有効な確認コード、設定保存が`IdentityResult.Failed` | `CustomException`。成功を返さず、保存エラーを保持する。 |
| 確認コード不正、未認証、対象ユーザーなし | 現行の拒否を維持し、設定保存を呼ばない。 |
| 現在パスワード不正 | 無効化もキー再生成も呼ばない。 |
| 無効化の保存失敗 | エラーを返し、キー再生成へ進まない。DB状態は変更前。 |
| 無効化保存後、キー再生成が失敗結果を返す | 失敗を表面化し、実storeを使う試験で二保存全体のrollbackを確認する。 |
| 後段のDB保存が例外、またはcommit前にキャンセル | 例外・キャンセルが伝播し、未commitの状態を残さない。 |
| 両保存・commitが成功 | 二要素認証は無効、キーは再生成後の状態。成功応答は従来どおり。 |

統合試験では実UserManager・EF storeを使い、後段の保存へ限定した失敗注入を行います。失敗後は**新しいscope・contextでDBを読み直し**、有効フラグ、認証キーの保存値、同操作で更新されるsecurity stamp等が変更前と一致することを確認します。具体的なキー値・パスワード・tokenをログや試験結果へ出しません。

新しいresourceキーはneutralのIdentity catalogへ追加し、キー参照・catalog parityの既存試験を通します。単体試験では失敗結果の検査と後続を呼ばないこと、統合試験では保存境界を担当させ、同じ保証をmockだけで合格にしない構成にします。

**この追記の状態:** 実装例と試験設計を追加しただけで、handler、DI、resource、テストは未変更です。例のビルド成功・rollback成立を確認済みとは扱いません。既存の重点46件成功も、この修正案の検証結果ではありません。

### ロール・権限・グループ: 一まとまりの編集と集合条件を分けて守る

**修正方針:** userのrole割当とrole permission集合更新の複数保存をまとめ、結果を確認します。管理者最低人数は、別userへの同時変更でも守る必要があるため、テナント内の管理者集合を対象にロックまたは適切な直列化方式を選びます。root seed adminの既存保護は維持します。

権限cacheの無効化はcommit後に行い、失敗後に再同期できるようにします。全要求に同じ長いtransactionやグローバルロックを適用するのではなく、保護すべき範囲を限定します。

グループは、後勝ちを許すかを確認してから必要なら版管理を追加します。member同時追加の制約違反は、実際の重複だけを分類して既存結果へ収束させます。

**受入条件:** 二人の管理者を別接続で同時解除しても最低人数を破らない。permission置換途中の失敗で中間集合を残さない。cache失敗後の再試行・補修で正本へ収束する。グループについて未調査の全操作を一括で保証済みにしない。

### refresh token・session: 一つの旧値からの更新を制御する

**修正方針:** 旧トークンや版を条件にした更新と、対象sessionの新ハッシュ保存を一つの境界へまとめます。競合で更新件数が0なら、保存失敗と区別して採用した契約に従って扱います。DB処理の一体化と、token生成・応答再送の契約は別に検討します。

応答喪失時に同じ結果を返す案を採用する場合も、生のrefresh tokenを安易に操作履歴へ保存しません。安全な結果保持方式を設計できない場合は、競合・結果不明時の再認証を含む明示的な契約を選びます。sessionなし互換とlogin時のbest-effort session生成は、今回の変更による影響を確認して判断します。

**受入条件:** 同じ旧tokenを二要求が同時使用しても複数の有効な更新系列を作らない。user tokenとsessionの中間失敗で片側だけ確定しない。sessionなし、失効済み、管理者取消との競合、commit後応答喪失を区別して試験する。

### プロフィール画像・代理操作権限cache: 失敗後も直せるようにする

**修正方針:** プロフィールのphone更新と最終保存をまとめ、任意`If-Match`の現行契約を維持するか変更するかを明示します。新画像はDBが参照を採用するまで暫定扱いとし、旧画像削除はcommit後の追跡可能な作業へ分けます。遅延した削除が再利用中の画像を消さないよう、対象キー・版・現在参照を検査します。

代理操作権限は、終了・取消済みの再要求でもcacheの再同期に進めるか、反映待ちを耐久的に残します。失敗時に「DB保存済みだから成功」の意味が実際の認可判定と矛盾しないよう、反映期限と判定経路を確認します。

**受入条件:** profile保存失敗で旧画像が生存し、新画像の孤立を後から回収できる。DB終端状態の保存後cache障害を起こしても、再実行・補修で認可判定が終端状態へ収束する。画像URL受信の逆順問題は、利用可能なFiles.Contractsで対処できるかを確認し、不足時は別の依存作業とする。

### 維持・追加確認する経路

ユーザー無効化・削除の自己操作・管理者保護と保存結果確認は、回帰試験で維持します。IUserServiceのfacadeではなく責務別serviceを修正し、既存のpermission gate、認証アプリ境界、キャンセル伝播を崩しません。

メール確認・再送、各group操作、個別画像設定、session cleanupなどは、上記変更の直接の呼び出し元・影響先を追跡します。新しい課題が確認された場合は、追加の修正として範囲・根拠・受入条件を記録します。

<a id="multitenancy"></a>

## 6. Multitenancyの修正内容

### 専用DBのイベント受信先: 他の非同期試験の前提を整える

**修正方針:** `FinbuckleEventTenantScope`でIDだけのtenant情報を作る経路を見直し、catalogから解決した完全なmetadataを、受信handler・Inbox・DbContextの解決前に有効にします。Outboxの読み出し元を選ぶdrain scopeは別の役割として維持します。

現在の同期的な`Begin`契約では非同期catalog解決とAsyncLocalの適用範囲を安全に表せるか確認が必要です。同期ブロックや「tenant設定をawaitするだけ」の迂回策は採りません。必要なら、tenant設定と受信処理全体を同じ非同期実行範囲へ包む契約を、共有基盤の独立した承認対象として提案します。

**受入条件:** 共有DBと二つの専用DBに識別可能なデータを置き、正しいhandlerとInboxが正しいDBに作用する。未知・削除済みtenantで既定DBへ黙って書かない。正常・例外・キャンセル後に元contextを復元し、他tenantの並行処理へ漏らさない。

### テーマ画像: 旧実体を消す順序を直し、清掃を追跡する

**修正方針:** 新画像の作成、DB参照の確定、旧画像の削除という順に変えます。DB確定と清掃予定を同時に残せるようにし、新画像が採用されなかった場合も回収対象を追跡します。外部削除をDB transactionへ含めようとはしません。

**受入条件:** DB保存失敗でも旧URLの実体が残る。外部削除失敗やprocess停止後も対象キーを失わず、同じ清掃の再実行で最新参照中の実体を消さない。cache無効化失敗も再同期可能にする。reset経路も変更の影響範囲として調べる。

### 期限延長: 異なる延長と同じ延長の再送を区別する

**修正方針:** tenant・操作ID・入力を関連付けた延長記録を持ち、同じIDの再要求は同じ操作として扱います。同じIDで月数・planなどが違えば拒否します。異なる延長はtenant単位の競合制御と再計算で両方反映します。

catalog store更新、操作記録、Outboxが同じ接続で確定できるかを確認します。tenantの専用DBに機械的に記録を置くのではなく、期限の正本であるcatalogとの境界を基準にします。単一transactionにできない場合は、catalog上の耐久進捗と再配送可能な状態を設計し、分散transactionがあると仮定しません。

**受入条件:** 同じ延長を二回送っても一回分、異なる二延長は合計分になる。期限直前・失効後・月末・年越しでも既存のclockベースの計算を守る。commit後応答喪失、cache失敗、Outbox追加失敗でも、延長済みの操作をもう一度足さずに補修できる。

### 期限通知: 通知済み記録だけが残る状態をなくす

**修正方針:** tenant・通知種別・対象期限の記録と、必要イベントの起動記録を同時確定することを第一候補にします。catalog上のnoticeとtenant DBのOutboxなど、接続が違う場合は、起動待ち・起動済みを持つ耐久的な中継記録を使う候補へ切り替えます。

**受入条件:** Outbox失敗後の次回scanが不足を補修し、同じ対象期限のイベントを論理的に重複生成しない。scanの並行実行、期限変更、tenantの接続変更も試験する。これはイベント起動の保証であり、Notifications側のメール到達保証とは分離する。

### 初期構築: 未起動を回収し、再開できる入力を確保する

**修正方針:** provisioning記録と起動要求を同じcatalog側で耐久化し、未登録のジョブを発見する処理を設けます。enqueue後のJobId保存失敗に備え、重複ジョブができても同じ実行を同時に進めないclaim・版などを設計します。単にPendingを定期的にすべて再実行する方式にはしません。

同じ実行の再開では完了手順を保持し、新しいやり直しは別の操作として明示します。Database・Migrations・Seeding・CacheWarmの各手順について、外部効果の後、完了記録前に止まっても安全かを調べます。完了マーカーがあるだけで手順の一回性を認定しません。activationのCompleted条件も維持します。

**秘密入力の注意:** 現行の`TenantInitialPasswordBuffer`はメモリ上で、取得時に取り除く仕組みです。したがって、ジョブ起動を耐久化するだけで初期構築全体が再起動可能になったとは言えません。process停止・seed途中失敗・別worker実行時にどう再入力するかを設計します。再入力待ちの状態、または承認された秘密保管基盤への期限付き参照などを候補とし、平文パスワードをjob引数・Outbox・操作履歴・監査へ保存しません。

**受入条件:** 起動要求保存後・enqueue前、enqueue後・JobId保存前、各手順の効果確定後・完了記録前に停止して試験する。重複起動が一つの実行へ収束し、秘密入力を失った場合も黙って成功・既定値へ進まず、認可された回復経路を提示する。job storage障害時のinline方針と、キャンセル・失敗記録の扱いも検証する。

### 有効・無効切り替えの扱い

この機能は元調査では棚卸しのみです。初期構築の変更に伴って、進行中の有効化拒否、完了直前の無効化、再開時に勝手に有効化しないことを確認します。既存の仕様・実装を調べずに独立した不具合修正を追加しません。

<a id="auditing"></a>

## 7. Auditingの修正内容

### SQL保存: 保存済みIDがあっても未保存分を進める

**修正方針:** `SqlAuditSink`のtenant単位batch保存を、同じ記録IDの再投入を受理できるようにします。PostgreSQLでの競合時insert処理、または重複だけを分類した再読込など、既存の型・保存方式に合う最小の方式を選びます。

事前の存在確認だけでは同時insert競合を防げません。また、すべてのDB例外をduplicateとして抑制する方式にはしません。同じIDで内容が異なる場合は黙って上書きせず、採用した同一性の規則に従って不一致を検出・隔離します。

**受入条件:** tenant A保存後にBが失敗したbatchを再実行しても、Aで止まらずBまで保存する。同一IDの並行投入、非duplicate DB障害、tenant未解決・削除済み、取消後の再投入を試験する。tenant未解決を正常配送済みとするかは契約で明示し、別tenantやrootへ勝手に振り替えない。

### 監査の意味: 変更試行とcommit済み変更を区別する

**修正方針:** `AuditingSaveChangesInterceptor`が保存前に取得した差分を、そのままcommit済み変更の証明として表示しないようにします。差分の取得時点と、成功と判断できる時点を分け、操作・相関情報で結び付けます。

`SavedChanges`へ移すだけでは、明示的な外側のtransactionの最終commitまでは保証できません。通常保存と外側transactionの双方について、commit/rollback・結果不明をどう表すかを決めます。採用した意味に合わせ、一覧・詳細APIの表現が変わるならContracts、permission、利用者向けdocsも更新します。

**既存記録:** 過去レコードの成功を後から推測して確定しません。「旧方式・成否未確認」と識別する等の移行方針を持ち、新しい確定状態と区別します。

**受入条件:** 通常保存失敗、明示transactionのrollback、commit後応答喪失を試験し、試行を成功として記録しない。成功・失敗を二重に数えず、記録できない結果を不明として扱える。

### 耐久受理: 必須証跡を選んだ場合の追加作業

診断用の現行best-effortを維持する区分と、必須証跡にする区分を設定・契約で区別します。**必須証跡を採用しない場合でも、SQL再投入の不備と成功の意味の整理は別に進めます。**

必須証跡では、メモリキューの手前で耐久的に受理する入力・状態を設計します。業務変更と同じDB・接続に起動記録を置ける場合と、認証失敗やHTTP活動のように同じ業務transactionがない場合を分けます。三モジュール・専用DB・catalogを一つの分散transactionで囲む計画にはしません。

耐久キューの候補として既存のOutbox等を使う場合も、監査context自身の変更を再監査しない制約を維持し、配送結果・重複・退避・遅延を確認します。別の全面的なメッセージ基盤を先に新設するのではなく、既存機構で必要保証を満たせるかを先に評価します。

**受入条件:** 合意した受理時点後のprocess停止で対象記録を失わない。DB・退避先障害、再試行上限、再投入後も同じIDで収束する。受理前失敗は明示的に返し、業務commit後の監査障害を単純な業務失敗として再実行させない。

### 保持削除・DLQ再投入・運用

**修正方針:** 監査種別ごとの保持日数、削除期限、削除保留条件、tenant削除後の扱いを確定します。既存の小分け削除は生かし、tenant別の進捗・失敗を記録します。要件がなければ、汎用legal-holdシステムまで拡張しません。

DLQの永続volume、容量・アクセス権、暗号化等は稼働環境の設計対象とします。再投入は元IDを維持し、件数・成功・未解決・不正データを区別し、途中停止後も再開できるようにします。ファイルがあるだけで復旧可能とは扱いません。

**受入条件:** 保持期限の境界時刻・再実行・tenant途中失敗・削除保留を試験する。再投入記録と警告を確認し、backup復元時の期限超過・処理済み記録の復活への対応を手順化する。masking、secret除外、監査contextの再帰除外は回帰試験で維持する。

<a id="shared"></a>

## 8. 共有基盤の扱いと承認境界

この計画の作成依頼は、`src\BuildingBlocks`の変更承認ではありません。

| 論点 | まずモジュール側で確認すること | 共有変更が必要な場合の扱い |
|---|---|---|
| Outboxとの同時確定 | 既存の接続共有・ambient transaction参加で実現できるか。catalog storeを含め実接続で確認する。 | 必要な不足だけを独立提案。汎用UnitOfWorkを先に追加しない。 |
| consumerのtenant metadata | module実装で完全解決とscope維持を安全に満たせるか。 | `IEventTenantScope`とevent bus呼出順の変更が必要なら、明示承認後に最小変更。利用元を横断検証する。 |
| Inboxの例外分類・一回効果 | モジュールの業務一意性で守る部分と、framework保存の欠陥を区別する。 | `EfCoreInboxStore`の非duplicate例外抑制は独立修正候補。業務効果とmarkerの原子性を同時に解決したとは扱わない。 |
| 監査のcommit連携 | Auditing側で取得・結果の意味を整えられる範囲を確認する。 | 共通transaction通知等が必要なら、監査要件を確定してから提案する。 |
| APIの冪等性 | 業務操作IDと現在のresponse replayの違いを明示する。 | 共通filterの変更は原則別作業。今回の機能をfilterだけで安全にしたとは判断しない。 |
| Outbox担当権・lease | この計画の受入試験で長時間・重複配送への前提を確認する。 | lease更新や古い担当の確定拒否を実装するなら、全配送元に影響する別の承認対象。 |

承認待ちの共有変更がある場合は、関連機能の未達保証を残し、無関係なローカル修正だけ進めます。型キャスト、同期ブロック、黙ったdefault DBへの切替などで制約を迂回しません。

<a id="acceptance"></a>

## 9. 受入試験と完了条件

### 試験の使い分け

ユニット試験では、保存結果の検査、例外分類、状態遷移、再開判断を確認します。PostgreSQL・Redis・実ストレージを使う隔離試験では、保存境界・別接続競合・専用DB・外部障害を確認します。SQLiteやmockの成功を、その代用にはしません。

並行試験は単にTaskを二つ動かすのではなく、読取後の同期点を置き、別DbContext・別接続を使います。再起動試験は新しいscopeを作るだけで済ませず、揮発状態を失う条件を作ります。tenant contextはAsyncLocalの適用範囲に注意し、設定と実処理を同じ実行範囲に置きます。

| 対象 | 必須の失敗・競合シナリオ | 合格条件 |
|---|---|---|
| 登録 | 通常・外部認証、各保存間の停止、同時再送。 | 一登録・一論理イベント。必須データがそろうか、認可された補修へ進める。 |
| パスワード・二要素認証 | 各IdentityResult失敗、途中保存失敗、resetと変更。 | 成功を偽らず、ポリシー上必要な状態が一体に確定するか不足を補修できる。 |
| 権限集合 | 異なる管理者の同時解除、permission置換失敗、cache障害。 | 最低人数と要求集合を保ち、cacheを補修できる。 |
| token/session | 同一旧tokenの並行使用、片側更新失敗、応答喪失。 | 一つの有効な更新系列、片側だけの確定なし。採用した互換・敗者契約に一致。 |
| プロフィール・代理操作権限 | DB失敗、画像削除失敗、終端保存後cache障害。 | 旧実体を守り、新しい孤立を追跡し、終端判定へ収束する。 |
| ユーザー状態・グループ | 既存保護の回帰、採用した編集競合仕様。 | 自己・管理者保護を維持。仕様未決の上書きを勝手に変更しない。 |
| 専用DB受信 | 二専用DB、共有DB、未知tenant、例外・キャンセル。 | 正しいDBへ作用し、contextを復元。tenant間に漏れない。 |
| 期限延長 | 同操作再送、異操作同時実行、月末・年越し、後続保存失敗。 | 同操作は一回、異操作は合計分。起動欠落を二重延長せず補修。 |
| 期限通知 | Outbox失敗、次回scan、並行scan、期限変更。 | 記録だけで永久skipせず、対象期限ごとに必要な起動を残す。 |
| テーマ | 新規upload後のDB失敗、削除失敗、遅延した清掃。 | 旧実体を保ち、参照中の実体を誤削除せず、残りを回復できる。 |
| 初期構築 | enqueue前後の停止、重複job、各手順の効果後停止、秘密入力消失。 | 未起動を再発見し、同じ実行で再開または要対応へ進む。完了前に有効化しない。 |
| 監査SQL保存 | A保存後B失敗、同じIDの再投入・並行insert、非duplicate障害。 | 保存済みがあっても未保存分を進め、不正な成功抑制をしない。 |
| 監査の成否 | 保存失敗、外側transaction rollback、commit後応答喪失。 | 試行をcommit成功と呼ばず、既存の成否不明も区別できる。 |
| 必須監査 | 合意した受理点後のprocess停止、sink・DLQ障害、再投入。 | 合意した障害範囲で失わず、上限後も要対応として追える。未採用なら保証外を明記。 |
| 保持・機微情報 | cutoff境界、削除保留、tenant失敗、再投入、機微値・監査再帰。 | 保持契約とmaskingを守り、再実行しても不整合や秘密情報の記録を作らない。 |

### 実行する既存runner

以下は実装時に使うコマンド例です。本計画作成時の実行結果ではありません。各変更では該当クラスの`--filter`で最小範囲から始め、モジュール・統合・横断試験へ広げます。

```powershell
rtk proxy dotnet test src\Tests\Identity.Tests\Identity.Tests.csproj --verbosity quiet
rtk proxy dotnet test src\Tests\Multitenancy.Tests\Multitenancy.Tests.csproj --verbosity quiet
rtk proxy dotnet test src\Tests\Auditing.Tests\Auditing.Tests.csproj --verbosity quiet
rtk proxy dotnet test src\Tests\Architecture.Tests\Architecture.Tests.csproj --verbosity quiet
rtk proxy dotnet test src\Tests\Framework.Tests\Framework.Tests.csproj --verbosity quiet
rtk proxy dotnet test src\Tests\Integration.Tests\Integration.Tests.csproj --verbosity quiet
```

統合試験には利用可能なDocker隔離環境が必要です。共有基盤を承認の上で変更する場合は、保護規約に従い、最終的に`rtk proxy dotnet test src\FSH.Starter.slnx`で全体も検証します。未実行や環境阻害は成功として扱わず、残る受入条件を記録します。

### 変更ごとの完了条件

1. 必要保証と既存仕様の維持・変更点を文章で説明できる。
2. 修正前に失敗する対象試験があり、修正後に合格する。
3. 必要な実DB競合・停止・構成試験を通過し、未実施を明示する。
4. 新しい状態・操作ID・migrationに対する既存データと互換性の扱いが決まっている。
5. 最終失敗、再開、補償、監視の経路があり、担当と権限を決めている。
6. docs・changelog・関連する契約とルールが実装と一致する。

ユニット試験だけ通った段階を、機能全体の保証が完成した状態とはしません。

<a id="rollout"></a>

## 10. 既存データ・移行・段階導入

### 最初に現状を壊さず検査する

本番データの検査・補修は、この計画作成には含みません。実装後の導入作業として、認可されたread-onlyの照合と、承認済みの補修を分けます。

| データ | 確認・移行の方針 |
|---|---|
| 部分登録・期限／履歴不足 | 欠落を検出し、正本・適用ポリシーが確定した項目だけ補修。過去のパスワードや履歴を推測して生成しない。 |
| token/session不一致 | 安全に対応付けできないものは、承認した失効・再認証の運用へ。生tokenを復元・記録しない。 |
| 権限集合・cache | DBを基準に照合し、最低人数を壊す自動再付与や一律権限拡大をしない。 |
| 旧登録イベント | payloadの意味とsourceを調べ、重複を検出。IDが違うという理由だけで機械的に削除せず、配送済み効果も考慮する。 |
| 既存の期限延長 | 過去要求に操作IDがなかったものを、後から一意に復元できるとはしない。新方式の開始時点と旧clientの扱いを明示。 |
| 起動なしprovisioning | ジョブの有無、進捗、実際の手順効果、秘密入力を照合してから再開。古いPendingを一律enqueueしない。 |
| noticeだけの期限通知 | 対象期限と実際の配送記録を照合。未確認を一律再通知せず、補修時の通知重複方針を決める。 |
| 画像の孤立・清掃 | 現在参照・対象版・保持猶予を確認。照合対象の一覧と削除実行を分ける。 |
| 旧監査・DLQ | IDを維持し、成否未確認を保持。内容の異なる同IDを上書きしない。 |

### migrationと互換性

必要な新規記録・一意制約・版・監査状態が確定した後に、中央の`src\Host\FSH.Starter.Migrations.PostgreSQL`へcontext別のmigrationを追加します。新しいnullable項目や記録を先に追加し、照合・backfill・新コード切替後に制約を強めるなど、既存データに合わせた段階導入を選びます。

API起動時にmigrationを実行せず、DbMigratorでcatalog・共有DB・専用DBの適用を確認します。新しい操作記録を意図せずtenant filterの対象外へしないこと、catalogの記録を専用tenant DBへ置かないことをレビューします。

操作ID、refresh応答、初期構築Retryの意味、監査成否のAPI変更は、Contracts・OpenAPI・clientへの影響を確認します。旧要求を受け付ける期間に保証が弱くなるなら、その範囲を明示します。UIの変更が必要になった場合は別の依存作業として承認を得ます。

### 段階リリースと戻し方

保存結果検査や二重発行の解消は小さく先行し、新規状態・再開workerは検証済みの対象から有効化します。新しい耐久入力を受け付ける前に処理側を用意し、停止時も未処理記録を残します。

rollbackでは、作成済みの操作記録や監査状態を削除して旧版に戻すのではなく、受付停止・worker停止・互換読取などを設計します。新旧workerが並行して二重処理しないこと、旧版が新状態を誤解しないことを確認します。

監視対象は、登録補修待ち、権限cache反映待ち、token競合、起動なしprovisioning、期限通知起動待ち、画像清掃滞留、監査重複・不一致・DLQ・保持削除失敗です。操作IDやcorrelationで追跡し、秘密値は出力しません。

<a id="deliverables"></a>

## 11. 変更の分割と最終成果物

一つの大きなPRで三モジュールを切り替えず、次のような独立してレビュー・検証できるまとまりに分けます。表の並びを必須の一列依存とはしません。厳密な依存は第4章に従います。

| 変更のまとまり | 主な変更先・成果物 |
|---|---|
| Identityの成功判定 | password・期限・role・2FAの該当service/handler、焦点を絞った失敗試験。 |
| 登録の発行元整理 | 登録serviceとdomain handler、通常・外部認証登録の回帰試験。 |
| tenant受信契約 | Multitenancyのscope、必要なら承認済みframework変更、専用DB試験。 |
| 同期保存境界 | 登録、password/2FA、role、tokenを機能別に分割。各transaction・競合試験。 |
| Multitenancyの個別修正 | テーマ、期限通知、期限延長、初期構築を別変更へ。必要なmigrationと補修手順。 |
| cache・画像の回復 | profile、代理操作権限、必要なgroup・permission反映。 |
| 監査SQL再投入 | sinkのduplicate分類、部分batch・並行投入試験。 |
| 監査の意味と耐久受理 | 成否契約の変更と、採用した区分の耐久化を依存が分かる変更へ分割。 |
| 監査運用・保持 | DLQ再投入、retention、照会・警告・権限・運用手順。 |

新しいendpoint・permission・entityを追加する場合は、関連するSkillsと規約に従います。handler・validatorの組、Contracts境界、ローカライズ、構造化ログ、キャンセルを維持します。新しい汎用serviceやworkerは、既存の仕組みでは必要保証を満たせないと確認したものに限定します。

最終成果物は、コードとmigrationだけでなく、機能別の保証契約、実行した試験と未実施条件、既存データ照合・補修手順、監視・再投入手順、更新した仕様を含みます。

ユーザー向けのendpoint・設定・挙動・インフラが変わる場合は、repository規約に従い、別の`fullstackhero/docs`リポジトリの関連ページと`src/content/docs/changelog/`の更新を依存作業として含めます。現在のworkspace内の計画書だけでdocs更新完了とはしません。アクセス・変更権限がなければ未完了として明示します。

Skillsへの反映は、必要保証と受入試験を満たした方式から行います。仮説のままの監査要件や未検証のtransaction例を標準化しません。

<a id="references"></a>

## 12. 根拠と調査項目の対応

### 調査との対応

| 本計画の領域 | 詳細版の機能ID・共通項目 | 主な判断根拠 |
|---|---|---|
| 登録 | I01、G01・G09 | 二つの発行経路、初期権限とjob・Outboxの複数保存。 |
| password・2FA | I02・I07 | 保存結果無視、期限・履歴・キーを含む複数保存。 |
| role・group | I03・I04 | 中間集合、管理者集合の競合、cache反映。 |
| token/session | I06 | 旧token検査と二つの保存境界、sessionなし互換。 |
| profile・代理操作権限・状態 | I05・I09・I08 | blobとDB、終端早期return、維持すべき無効化ガード。 |
| tenant受信先 | G13 | IDだけのconsumer contextと完全なdrain metadataの差。 |
| 初期構築・activation | T01・T05 | 起動登録の隙間、手順marker、手動Retryの新規記録。T05は追加調査。 |
| 期限・通知・テーマ | T02・T03・T04 | 加算競合、通知marker先行、旧blob先行削除。 |
| 監査 | A01・A02、G12 | memory受理、tenant別部分成功、成否の意味、DLQ・保持契約。 |
| 共通依存 | G03〜G08 | API応答再利用、Outbox配送、Inboxの保存順と例外抑制。 |

詳細なソース行は[詳細版の参照一覧](data-write-quality-investigation-results.md)にあります。計画書作成時には、規約に加え、次の実装も確認しました。

- [イベント受信時のtenant scope][tenant-scope]: IDのみを設定し、専用DB接続を解決しない現在の境界。
- [初期構築service][provision-service]: 保存・enqueue・JobId保存、inline分岐、Retryの動き。
- [初期構築job][provision-job]: 手順実行と完了記録の順序。
- [初期パスワードbuffer][password-buffer]: メモリ保存と取得時削除。初期構築の再起動保証を設計する際の追加確認点。
- [監査SQL sink][audit-sink]: tenantごとの保存と部分成功後の再投入条件。

### 実装時に参照する規約

`.agents\rules\architecture.md`、`database.md`、`eventing.md`、`testing.md`、`integration-testing.md`、`buildingblocks-protection.md`と、`modules\identity.md`、`modules\multitenancy.md`、`modules\auditing.md`を参照します。

規約に「冪等性を提供」「落とさない」と記載されていても、その適用範囲はソースと試験で確認します。例えば、memory channelの待機はprocess停止後の耐久性を意味せず、Inboxの存在は業務効果とmarkerの原子性を意味しません。

[tenant-scope]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventTenantScope.cs#L8-L42
[provision-service]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningService.cs#L33-L174
[provision-job]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningJob.cs#L33-L90
[password-buffer]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantInitialPasswordBuffer.cs#L1-L19
[audit-sink]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/SqlAuditSink.cs#L23-L71
[twofactor-enable]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/TwoFactor/VerifyEnroll/VerifyEnrollTwoFactorCommandHandler.cs#L22-L51
[twofactor-disable]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/TwoFactor/Disable/DisableTwoFactorCommandHandler.cs#L22-L47
