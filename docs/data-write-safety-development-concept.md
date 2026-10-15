# 安全なデータ処理を実現する開発コンセプト

作成日: 2026-10-15

## この資料の位置付け

本書は、データ書き込み品質の調査をもとに、**今ある部品、足りない保証、それらをどうつなぐか**を説明する将来像の資料です。新しい基盤や全面刷新を先に決めるものではありません。

現状の根拠はコミット`a967537526f3b769c7d268da3ed2a6920af65ce9`を対象とした[詳細調査結果](data-write-quality-investigation-results.md)です。[解説版](data-write-quality-investigation-report.md)が機能別の問題を、[三モジュールの修正計画](identity-multitenancy-auditing-remediation-plan.md)が具体的な着手内容を扱うのに対し、本書は開発全体の考え方と目標構成を扱います。

「現在」と「目標構成」を区別して記載します。ここに書いた仕組みが実装・検証済みという意味ではありません。アプリケーション、DB、共有基盤、Skillsの変更は行いません。

## 目次

1. [開発コンセプトの要点](#principle)
2. [現在そろっている部品と保証の境界](#inventory)
3. [不足しているものを三種類に分ける](#gaps)
4. [具体的な改修・追加対象と役割](#responsibilities)
5. [処理の種類ごとのつなぎ方](#composition)
6. [途中で止まったときにどう回復するか](#recovery)
7. [機能ごとに必要な構成だけを選ぶ](#selection)
8. [今後の開発の進め方](#roadmap)
9. [何を確認すれば完成と言えるか](#acceptance)

<a id="principle"></a>

## 1. 開発コンセプトの要点

> **正しい値を、正しい対象へ、一つの論理操作として確定する。残りの必須作業は消えない形で引き継ぎ、再実行しても効果を増やさず、失敗しても追跡・回復できるようにする。**

現状は、DB保存、制約、Outbox、Inbox、ジョブ、監査といった基礎部品があります。しかし、すべての機能について、それらをつなぐ契約まで完成しているわけではありません。

課題は、既存部品の使い方だけではありません。既存部品自体の保証が不足する箇所と、業務に固有の操作記録・進捗・補償が必要な箇所もあります。したがって、「既存部品を使い直す」「既存部品を補強する」「必要な業務状態を追加する」の三つを使い分けます。

### 「安全な完了」の意味

永久障害があっても必ず成功する、という意味にはしません。目標は、合意した条件の下で次を成立させることです。

| 性質 | 実現したいこと |
|---|---|
| 正確性 | 保存できる値ではなく、対象期間・単位・業務計算に照らして正しい値を保存する。 |
| 安全性 | 途中失敗、同時実行、再送でも、部分更新・二重効果・取りこぼしを起こさない。 |
| 完了性 | 受理後の仕事を消失させず、完了か、明示された失敗・要対応状態へ進める。 |
| 確認・回復可能性 | 未実行、処理中、完了、失敗、結果不明を識別し、照会・再開・補償・訂正できる。 |

「要対応」は業務上の成功ではありません。自動処理の限界を隠さず、担当者へ引き継げる状態です。また、受理済み入力の耐久性、依存サービスの回復、正しいtenant情報、重複判定記録の保持など、保証の前提を機能ごとに明示します。

<a id="inventory"></a>

## 2. 現在そろっている部品と保証の境界

ここでいう「部品」は、専用のクラスだけでなく、EF・DBの能力、契約、運用機構も含みます。「存在する」は、全機能に正しく適用済みという意味ではありません。

| 部品・能力 | 現在あるもの | そのままでは保証しないこと |
|---|---|---|
| 入力・業務ルールの検査 | validator、domainの状態・数量等の検査。 | 正しい請求期間や測定時点の選択。検査後に他の要求が状態を変える競合。 |
| DB制約 | 台帳参照ID、有効契約、会話・membership等での一意制約。 | 異なる操作の加算取りこぼし、管理者数や階層全体の条件、競合敗者への安定した応答。 |
| 単一保存のtransaction | 関係DBの一回のSaveChangesによる一体的な保存。 | 別の保存呼び出し、先行読取、別DB、Redis、画像、HTTPまでの一体性。 |
| 複数保存をまとめる能力 | EFの明示transaction。ファイル移動には実際の適用例。 | 全業務処理への適用。Identity manager等が内部保存する場合の境界。 |
| Outboxのtransaction参加 | 同一scope・接続のambient transactionへ、Outbox contextが参加する実装。 | 呼出元がtransactionを開始しない場合の業務保存との同時確定。別DBをまたぐ原子性。[根拠][outbox-enlist] |
| APIの応答再利用 | 一部endpointの冪等性filter、成功応答cache、実行予約。 | commit後・cache保存前の停止、予約期限切れ、cache障害、同じキー・異入力、保持期限後の業務重複。[根拠][idempotency] |
| Outboxの配送 | 保存済みeventの取出し、再試行、PostgreSQLでのclaim、上限後の隔離、内部の再投入API。 | 保存されなかったeventの回復、受信効果の一回性、期限切れ後の旧workerの確定防止、運用者による対応。[根拠][outbox-claim] |
| Inboxの処理済み記録 | event IDとhandler名による照会、handler後の記録。 | 業務効果と記録の同時確定。現在は保存例外の分類にも不足がある。[根拠][event-bus]・[保存処理][inbox-store] |
| ローカルdomain event | 保存後のprocess内publish。 | 耐久的な起動。外側transactionの最終commit後に必ず公開する保証。[根拠][domain-events] |
| ジョブ実行 | Hangfireと永続job storageの登録。初期構築等での利用。 | 業務保存後・enqueue前の停止、手順効果の一回性、入力の再起動後の利用。[根拠][jobs-config] |
| HTTP再試行 | 対象clientのretry・timeout等。 | 相手で成功した後の応答喪失、相手での二重反映防止、job retryとの増幅防止。 |
| テナント分離・接続選択 | tenant filterとtenant metadataに基づくDB選択、専用DBのOutbox drain。 | consumer側でも完全なmetadataを解決すること。IDのみを設定する経路が残る。[根拠][tenant-scope] |
| 派生状態の反映 | cache無効化、quota更新、SignalR、保存先変更eventによるURL更新。 | commitとの同時確定、逆順eventからの収束、差分検出と再計算。 |
| 監査・退避 | メモリchannel、SQL sink、有限retry、ファイルDLQ、masking。 | process停止後の必須証跡保持、部分batch再投入、commit成否の証明、DLQ保存自体の成功。 |
| 観測・試験 | log・metrics、unit・統合テスト基盤、実行済みの重点46件。 | 実運用の警告・担当・復旧期限。今回の業務上の停止・競合・実DB構成の保証認定。 |

例えば「DBの一意制約」は既存の能力なので、同じ目的の独自分散ロックを先に新設する必要はありません。一方、「複数DBと外部送信を一つに確定する仕組み」が既存部品の組み合わせだけで提供されているわけでもありません。

<a id="gaps"></a>

## 3. 不足しているものを三種類に分ける

### 既存部品の適用を直す

新しい基盤を作らなくても対応できる種類です。IdentityResultの確認、同じDB内の複数保存をtransactionでまとめること、統合イベントの発行元の一本化などが該当します。

この種類は、機能側を小さく直し、実接続・停止点の試験で確かめるのが基本です。共通のtransaction wrapperがないこと自体を不具合とはせず、既存のEFと接続参加で必要保証を満たせるかを先に確認します。

### 既存部品の契約・実装を補強する

Inboxの例外分類、受信handlerと処理済み記録の保存境界、専用DBのtenant解決、担当権期限切れ後の扱いなどです。

ここでは「呼び出せば何を保証するか」を共通契約として明確にします。受信側の一回効果は、frameworkで同じDBのtransaction参加を整える方法と、moduleの業務制約で重複を防ぐ方法を比較し、必要な場所に適用します。どちらも現在のInboxをそのまま使うだけで実現するわけではありません。

共有基盤の変更は他モジュールにも影響するため、`src\BuildingBlocks`の明示承認と横断検証が必要です。

### 業務固有の状態と回復を追加する

期限延長の操作ID、初期構築の起動・手順進捗、画像の清掃予定、期間確定値、必須監査の受理状態などです。これは単にクラスが足りないのではなく、**後から正しい判断をするための業務情報が足りない**問題です。

| 追加・明確化する能力 | なければできない判断 |
|---|---|
| 操作の識別と入力同一性 | 同じ延長の再送か、別の延長か。同じIDで違う内容を受け付けてよいか。 |
| 耐久的な起動要求と進捗 | 受理後に起動しなかったか、途中まで完了したか。何から再開するか。 |
| 外部結果の記録・照会・補償 | 外部は成功したか、結果不明か。再送か回収か、確認待ちにすべきか。 |
| 正本との照合・再計算 | cache、quota、件数、URLが正本とずれていないか。どう直すか。 |
| 正しい根拠データの保持 | 前月値などを後から確定・再計算できるか。誤りをどう訂正するか。 |
| 最終失敗・保持・運用責任 | 自動retry後に誰が何を見て対応するか。いつ記録を消してよいか。 |

これらをすべて共通frameworkの新しいserviceにする必要はありません。まずmoduleの責務として設計し、複数機能で同じ契約と実装が成立すると確認した部分だけ共通化します。

<a id="responsibilities"></a>

## 4. 具体的な改修・追加対象と役割

まず責務を分け、その後に実際の変更対象を示します。**4.1以降の既存ファイル名は現物に対応しますが、改修内容と「新規案」とした名前・フィールドは未実装の提案です。** 記載は実装承認を意味しません。

| 担当 | 主な役割 | 他へ任せてはいけない判断 |
|---|---|---|
| API・command入口 | 認証・認可、入力検査、操作IDの受取、受理・完了・結果照会の応答。 | cacheに同じ応答があるかだけで、業務操作の一回性を判断しない。 |
| moduleの業務処理 | 正本、期間・計算、不変条件、同時更新、操作の一回効果、完了条件。 | OutboxやInboxがあるだけで、業務上の正しさを保証済みとしない。 |
| DB・保存基盤 | 必要な制約とtransaction、同一接続への参加。 | 別DB・外部処理まで同時確定できるとは扱わない。 |
| 配送・job基盤 | 起動記録の読出し、実行、claim、有限retry、未完了の再発見。 | 外部の業務成功や、別workerの効果の有無を推測しない。 |
| 受信処理・外部連携 | 安定した識別、業務効果と重複防止、結果不明の照会、必要な補償。 | HTTP成功やbroker受理を、すべての後続業務の完了と同一視しない。 |
| 監査・運用 | 試行と成功の区別、追跡、滞留検出、要対応の引受け、保持・再投入。 | logがあるだけで復旧が成立するとしない。 |

### 関連付ける識別情報も分ける

| 識別情報 | 用途 |
|---|---|
| 論理操作ID | 一つの延長・登録補修などの業務操作を再送時も同じものと識別する。 |
| event ID | 一つの発行済みeventと、その再配送を識別する。別IDの同義eventまでは自動で重複排除しない。 |
| attempt ID | 配送・実行の各試行を追跡する。retryのたびに変わってもよいが、業務の重複判定には代用しない。 |
| correlation・trace | 複数処理の関連を調べる。関連するだけで同一操作とは限らない。 |
| 版・順序 | 古い要求・eventか、読取後に更新されたかを判定する。 |

操作IDはtenant・操作種別・必要なcaller境界と組み合わせ、認可された結果照会へ結び付けます。他人のIDを知れば結果を読めるようにしたり、秘密値を操作記録へそのまま保存したりしません。

<a id="component-changes"></a>

### 4.1. BuildingBlocksで改修する対象

以下のパスは`src\BuildingBlocks\`からの相対パスです。`Eventing`と`Eventing.Abstractions`は別プロジェクトです。共通部分を変更する場合は、いずれも事前の明示承認が必要です。

| 変更対象となる既存ファイル | 提案する具体的な変更 | その変更で埋める保証の切れ目 |
|---|---|---|
| `Eventing\Inbox\EfCoreInboxStore.cs` | `MarkProcessedAsync`の包括的な`DbUpdateException`抑制をやめる。対象の処理済みキーに対する一意制約違反だけを重複として扱い、それ以外は呼出元へ失敗を返す。PostgreSQLでは競合後のtransaction状態も考慮したinsert方式・rollback境界にする。 | DB停止、schema不整合等まで「処理済み」と扱い、配送を完了してしまうことを防ぐ。ただし、これだけでhandlerの二重効果は防がない。 |
| `Eventing.Abstractions\IEventTenantScope.cs` | 現在の同期`Begin`契約を、非同期にtenant metadataを解決して処理を実行できる契約へ変更する。例えば`ExecuteAsync`でcallbackを受ける方式を候補とする。 | tenant IDだけでなく接続情報等を取得し、正しいambient contextの内側で処理を構築・実行できるようにする。 |
| `Eventing\InMemory\InMemoryEventBus.cs`、`Eventing\NullEventTenantScope.cs` | 上記契約へ追随し、callbackの内側でDI scope、handler、Inboxを解決する。非multitenant構成の既定実装も同じ契約を実装する。 | tenant設定後でも、先に構築されたDbContextが誤った接続を保持する問題を避ける。単にtenant設定helperを非同期化するだけでは済ませない。 |
| `Eventing\Outbox\OutboxMessage.cs`、`IOutboxStore.cs`、`EfCoreOutboxStore.cs`、`OutboxDispatcher.cs` | 複数担当・期限切れ引継ぎに対応するため、取得ごとに変わる`ClaimToken`等を追加する案。成功・失敗の確定をtoken一致かつ未完了の条件付き更新にし、更新件数を確認する。所有権喪失は通常の配送失敗として再確定しない。 | 期限切れ後に引き継いだ新担当の結果を、旧担当が上書きすることを防ぐ。既存の`ClaimedBy`は診断用で、同じprocessによる別取得も区別できないため代用しない。 |

tenant契約変更では、module側の`src\Modules\Multitenancy\Modules.Multitenancy\Services\FinbuckleEventTenantScope.cs`も改修し、catalogから完全なmetadataを取得します。解決できないtenantは誤った既定DBへ流さず、失敗・隔離の方針に従わせます。`MultitenancyModule.cs`とEventingの`ServiceCollectionExtensions.cs`は登録整合性の確認対象です。

Outboxの担当権変更には、entityと同じファイル内のEF configuration、および`src\Host\FSH.Starter.Migrations.PostgreSQL\Eventing\`へのmigrationが伴います。適用は既存の`EventingDbInitializer`とDbMigratorの経路を使い、共有DBだけでなく専用tenant DBも対象にします。長時間処理のlease更新を追加するかは処理時間の要件次第です。**tokenを追加しても外部への二重送信は止まらない**ため、受信側の重複防止は別途必要です。

`IEventingDrainScope`と`FinbuckleEventingDrainScope`は、送信元OutboxのDB選択を担う既存部品です。今回補強するconsumer側のtenant scopeと統合せず、双方の役割を維持します。

#### Inboxの「業務効果と処理済み記録」は、別の作業として扱う

上表の例外分類修正と、受信業務の一回効果は別です。初期案では**全handlerを包む新しい共通transaction部品を直ちに追加せず、必要な受信moduleのDBに重複防止記録・業務制約を置く**方針を取ります。

例えば新規案の`AppliedIntegrationEvent`を、採用するmodule内の`Domain`等とEF configurationに追加し、tenant・event ID・処理種別を識別する一意制約を設けます。受信handlerは、この記録と業務効果を同じmodule DbContextのtransactionで確定します。frameworkのInbox記録に失敗して再配送されても、module側では二度目の効果を発生させません。同義操作が別event IDで届く場合は、event IDではなく業務操作IDの制約も必要です。

これは新しい「全module共通Inboxテーブル」の提案ではなく、必要な受信業務だけに置く記録の案です。既存の業務一意キーで足りるなら追加テーブルも不要です。競合時は一意制約違反の分類とrollback・再照会まで設計し、最初の存在確認だけには依存しません。

将来、共通の受信transaction実行部品を採用するなら、`InMemoryEventBus`、`IInboxStore`、`EfCoreInboxStore`とmodule側の接続参加を一組で設計します。その場合も、異なるDbContextが同じ接続・transactionへ本当に参加することを条件とします。現在の`IInboxStore`に呼出しを一つ足すだけで成立するものとしては扱いません。

### 4.2. BuildingBlocksで新設・置換せずに使う対象

以下も`src\BuildingBlocks\`からの相対パスです。「据え置き」は、それを使う機能側の修正や実構成での検証まで不要という意味ではありません。

| 既存の部品 | 今回の使い方・据え置く理由 |
|---|---|
| `Persistence\ScopedDbConnectionProvider.cs`、`AmbientDbTransactionRegistry.cs` | 同一scope・接続のtransaction共有に再利用する。機能側でtransactionを開始し、必要なcontextの参加を確認する。新しい汎用UnitOfWorkの追加を前提にしない。 |
| `Eventing\Outbox\EfCoreOutboxStore.cs`の`AddAsync`とtransaction参加処理 | 発行側の同一DB transactionへ参加させる既存機構を使う。担当権制御の改修とは分け、業務保存とOutbox保存をまとめる呼出元側の修正を行う。 |
| `Jobs\Extensions.cs`によるHangfire登録 | 永続job実行に使う。業務保存とenqueueが一体にならない点は、moduleの耐久起動記録・再発見で補う。新しいjob engineは追加しない。 |
| `Web\Idempotency\IdempotencyEndpointFilter.cs` | 応答再利用として維持する。業務操作ID・DB側の重複防止の代用品にはしない。入力fingerprintや予約延長の改修は、API層で追加保証が必要な場合の別課題とする。 |
| `Persistence\Inteceptors\DomainEventsInterceptor.cs` | process内の通知として扱い、必須後続処理の耐久起動には使わない。Outboxへ移すべき処理は発行元を修正し、このinterceptorを万能なcommit通知器へ置き換えない。 |

この区別により、「Outboxを使うためにPersistence全体を作り直す」「安全にするために全保存へretryを付ける」といった範囲拡大を避けます。

<a id="auditing-component-changes"></a>

### 4.3. 共通機能として改修するAuditing

AuditingはBuildingBlockではなく、他moduleからContracts経由で利用する**共通module機能**です。以下の既存ファイルは`src\Modules\Auditing\`からの相対パスです。

| 変更対象となる既存ファイル | 提案する具体的な変更 |
|---|---|
| `Modules.Auditing\Persistence\SqlAuditSink.cs`、`AuditRecordConfiguration.cs` | 同じaudit IDの再投入を許容する保存にする。tenant Aの保存成功後にtenant Bで失敗しても、再投入がAの重複で止まらないようにする。未知tenantや接続障害を黙って欠落扱いにしない。 |
| `Modules.Auditing\Core\AuditBackgroundWorker.cs`、`Persistence\FileAuditDlqSink.cs` | 有限retry後の退避失敗を可視化し、再投入の単位・結果を追えるようにする。新規案の`AuditDlqReplayJob`等から認可された再投入を行い、保存済みIDを維持する。 |
| `Modules.Auditing\Persistence\AuditingSaveChangesInterceptor.cs`、Contractsの`AuditEnvelope.cs`・`AuditEnums.cs`、保存側の`AuditRecord.cs` | 差分を取得したという事実と、最終transactionがcommitしたという事実を分ける。必要なら結果区分・操作IDを追加し、serializer・DTOにも反映する。外側transactionを考慮せず、`SavedChanges`へ移すだけで成功証明にはしない。 |
| `Modules.Auditing\Core\ChannelAuditPublisher.cs`、`AuditingModule.cs` | メモリchannelを診断用として残す。必須証跡もこの経路だけで受理したことにしない。必須経路を追加する場合は、登録と呼出元の選択を明示する。 |

**必須証跡の耐久経路は、要件を決めてから追加する条件付きの部品**です。既存の`Modules.Auditing.Contracts\IAuditPublisher.cs`は低遅延・非ブロッキングのpublish契約なので、その成功を黙って「DBへ耐久保存済み」と読み替えません。

追加する場合の候補は、Contractsに新規案の`IAuditDurableWriter`、runtimeにその実装を置き、「どこまで保存されたら戻るか」を契約にすることです。ただし、writerが監査DBへ独立保存するだけでは業務commitとの一体性は得られません。業務と同時に確定すべき証跡は、発行側DBに監査用の起動記録を置きます。

保存先には、条件を満たすなら既存Outboxを利用し、Auditingの受信処理から重複許容のSQL sinkへ流す案を優先して比較します。別の耐久queueが必要な場合だけ、監査専用の保留recordと配送workerを追加します。**Outboxと監査専用queueを無条件に二重追加する計画ではありません。** 他moduleからAuditing runtimeを直接参照せずContractsを使い、発行側のtransaction参加は公開契約・adapterで実現します。

### 4.4. 各moduleに追加・拡張する部品

共通基盤だけでは決められない操作ID・進捗・清掃対象は、業務の持ち主に置きます。以下の新規名は配置を具体化するための仮称であり、既存ファイルではありません。

| 対象 | 既存の変更先 | 追加・拡張する具体物の案 |
|---|---|---|
| Identityの二要素認証 | `Modules.Identity`の二要素認証有効化・無効化handler、`IdentityDbContext`を使う境界、localization resource | まずは**新しい共通部品なし**。全`IdentityResult`確認と、無効化・キー再生成を同一contextでまとめるtransactionを追加する。[具体的な修正例](identity-multitenancy-auditing-remediation-plan.md#next-fix-example)を参照。 |
| Multitenancyの期限延長 | `Features\v1\RenewTenant\RenewTenantCommandHandler.cs`、`Services\TenantService.cs`、`Data\TenantDbContext.cs`、Contractsの延長command | 新規案の`Domain\TenantRenewalOperation.cs`とEF configuration。操作ID、tenant、延長入力の同一性、確定結果を保存し、業務更新と同じcatalog transactionで確定する。一意制約と加算の競合制御を組み合わせる。 |
| Multitenancyの初期構築 | `Provisioning\TenantProvisioning.cs`、`TenantProvisioningStep.cs`、`TenantProvisioningService.cs`、`TenantProvisioningJob.cs` | 既存の進捗recordへ未enqueue・試行・再開に必要な情報を追加する。新規案の`Provisioning\TenantProvisioningRecoveryJob.cs`で未起動・中断を再発見する。別の汎用workflowテーブルを重ねない。 |
| Multitenancyのテーマ画像 | `Services\TenantThemeService.cs`、`Domain\TenantTheme.cs`とその保存境界 | 新規案の`Domain\TenantObjectCleanupTask.cs`、EF configuration、`Services\TenantObjectCleanupJob.cs`。旧object key、操作ID、状態、試行・次回実行情報を保持し、新参照と清掃予定を同時保存する。 |
| Identityのプロフィール画像 | `Services\UserProfileService.cs`、`Features\v1\Users\SetProfileImage\SetProfileImageCommandHandler.cs` | 同様の清掃record・workerをIdentity所有にするか、Filesの公開Contractsに委譲できるかを先に決める。Multitenancy runtimeの清掃serviceを直接流用しない。 |
| 必須eventの受信業務 | 採用するmoduleのevent handler、DbContext、EF configuration | 4.1の`AppliedIntegrationEvent`等、または既存の業務一意キー。処理済み記録と業務効果を一体にし、event再配送を安全にする。全moduleへの一律追加ではない。 |

表の`Modules.Identity`は`src\Modules\Identity\Modules.Identity\`、Multitenancyのruntime内パスは`src\Modules\Multitenancy\Modules.Multitenancy\`を起点とします。各moduleの追加entity・列には、対応DbContextのmigrationと既存データの移行方針が必要です。

初期構築では、既存の`Services\TenantInitialPasswordBuffer.cs`がprocess内・一回消費である点も変更判断の対象です。耐久起動recordだけ追加しても再開可能にはなりません。再開時に安全な初期資格情報の設定へ移せる契約を決め、平文passwordをjob引数・Outbox・進捗・監査へ保存しないことを条件にします。

画像では、清掃task保存前に停止した新objectも残り得ます。仮objectを識別できるkey・有効期限・走査を併せて設計し、「旧画像の削除jobを追加すれば全孤児を回収できる」とはしません。

<a id="component-connections"></a>

### 4.5. 追加・改修後に、実際にどうつながるか

以下は5章の基本形を、具体的な部品へ対応させたものです。角括弧の「新規案」は現在存在しない部分です。

| 基本形・例 | 部品を通る順序 | 完了の判断 |
|---|---|---|
| 同期更新: 二要素認証の無効化 | 既存handler → `UserManager`と同じ`IdentityDbContext`でtransaction開始 → 無効化・キー再生成と各結果確認 → commit → 応答 | 同じDBで両変更が確定したこと。Outbox、Inbox、Hangfireは追加しない。 |
| 非同期引継ぎ: 必須event | 発行元handler → 業務transaction → 既存`EfCoreOutboxStore.AddAsync`の参加 → commit → 担当権を補強した`OutboxDispatcher` → `InMemoryEventBus` → 非同期tenant scopeでmetadata解決 → 受信handlerとDbContext構築 → module側の重複防止記録・業務効果を同時確定 → Inbox記録 | 配送完了ではなく、受信側の必要効果の確定。発行側が全体完了を管理するなら、結果照会または耐久的な完了通知も追加する。 |
| 外部実体: テーマ画像の差し替え | `TenantThemeService` → 仮画像準備 → 新参照と[新規案]`TenantObjectCleanupTask`を同じDBで確定 → 応答 → [新規案]`TenantObjectCleanupJob` → 現在参照でないことを再確認して旧画像削除 → 清掃完了保存 | 画像参照の切替と清掃完了を別に管理する。削除後の記録失敗は同じ対象の再確認で収束させ、通信失敗を「削除済み」と扱わない。 |
| 必須監査を伴う更新 | 業務transaction → 同時確定する監査用起動記録 → 配送 → Auditing受信処理 → 重複許容にした`SqlAuditSink` → 保存結果・滞留を追跡 | 合意した受理点の耐久性と、その後の監査保存。メモリchannelへの投入だけで完了とはしない。 |

これらは、すべての処理を一つのtransactionにする配線ではありません。**一つのDB内では一体に確定し、DB間・外部との間では記録を残して引き継ぎ、受信・再開側で効果を増やさない**という境界を実装へ落としたものです。

### 4.6. 着手単位と、追加しないもの

| 着手単位 | 位置付け |
|---|---|
| 保存結果確認・同一DB transactionの適用 | module内で進める。Outbox一本化の検討と切り離して、二要素認証等から着手できる。 |
| Inbox例外分類と受信tenant解決 | 共通部品の修正候補。専用DBへの作用先と、失敗を成功扱いしないことを先に固める。受信業務の重複防止は別途組み合わせる。 |
| Outbox担当権の補強 | 複数instance・期限切れ引継ぎを保証するための独立作業。発行元一本化や送信元transactionの適用とは別の課題。 |
| 延長操作記録・初期構築回復・画像清掃 | 必要保証を合意した機能ごとに追加する。基盤完成を待たず設計できるが、未検証の接続や起動保証を前提にしない。 |
| Auditingの再投入安全性・必須経路 | SQL sink等の補強と、耐久受理の要件判断を分ける。必須経路は対象と業務停止方針の合意後に追加する。 |

新しい汎用`OperationDbContext`、全機能共通Saga engine、全保存への自動retry、全handlerを覆う分散ロックは、現時点の標準追加物にはしません。共通部品の抽出は、複数の実装で同じ契約が成立した後の判断です。Skillsの改修も、これらの方式を検証してから既存の`add-feature`、`add-integration-event`、`add-entity`等へ判断基準・試験条件として反映し、未実装のhelperを存在するものとして案内しないようにします。

<a id="composition"></a>

## 5. 処理の種類ごとのつなぎ方

ここが目標構成の中心です。単一の万能な流れではなく、処理の境界に応じた三つの基本形を選びます。以下は現在の実行順序ではなく、必要な補強後に目指す形です。

### 基本形1: 同じDBへの同期更新で目的が完結する

対象例は、二要素認証の無効化とキー再生成、権限集合の置換です。

1. 認証・認可・入力を確認し、正しいtenantとDBを選ぶ。
2. 必要なら操作IDを使う。重複の事前照会だけに依存せず、同時要求もDB制約や条件付き更新で一つの結果へ収束させる。
3. 業務更新、必要な操作記録を同じtransactionで保存する。数値の加算や集合条件は、保存の原子性とは別にロック・条件付き更新等で保護する。
4. 保存結果をすべて確認してcommitする。必要な必須監査の記録も、採用した保証に合う境界へ入れる。
5. commit済み結果を応答する。応答を失った場合は、操作結果または正本を認可付きで照会する。

この形では、必須の非同期処理がなければOutboxやジョブは不要です。例として、二要素認証では同一contextのtransactionと保存結果の検査をまず使います。一方、在庫加算では一回保存するだけで十分とはせず、別要求の加算取りこぼしも防ぎます。

### 基本形2: DB更新の後に、別の必須処理を起動する

対象例は、登録後の必須処理、期限通知、テナント初期構築です。

1. 発行側で、業務更新と「何を後で行うか」という起動記録を、同じDB・接続のtransactionで確定する。Outboxを使うなら、既存の参加機構を正しく適用する。
2. この時点の応答は、後続がまだなら「受理済み」または「本体保存済み」として意味を明示する。全体完了とは呼ばない。
3. 配送担当が耐久記録から作業を取得する。プロセスが止まっても、別の担当が未完了を再発見できるようにする。
4. 受信側で完全なtenant情報を解決し、その後にhandler・Inbox・DbContextを構築する。
5. 受信側のDB効果と重複防止を同時に確定するか、業務操作の一意制約・条件付き更新によって再配送の効果を抑える。最初の照会だけで並行重複を防いだとはしない。
6. 後続の必須効果が確定したことを、状態照会または耐久的な完了通知で発行側へ伝える。完了通知が必要なら、その起動記録も受信側の効果と同じ保存境界で確定する。配送済みであることだけを全体完了の根拠にしない。
7. 上限後の失敗は要対応へ移し、入力と進捗を保持して照会・再投入できるようにする。

**この形には現状からの補強が必要です。** 現在のInboxは業務処理後の独立した記録であり、上記5の境界を自動では提供しません。また、OutboxからHangfireへ渡す構成でも、enqueue後・記録前に停止すると重複ジョブができ得ます。ジョブ側を同じ操作として収束させる必要があります。

別DBへの業務効果は、それぞれのDBで確定します。発行側DBと受信側DBを同じtransactionにするのではなく、耐久的な引き継ぎと受信側の一回効果でつなぎます。catalog上のnoticeと専用tenant DBのOutboxなども同様に、記録の配置と中継状態を設計します。

### 基本形3: 外部システムや外部実体を変更する

対象例は、画像の差し替えと削除、Webhook、メール、DB外の容量カウンタです。

1. まず外部へ何をするか、対象、安定した操作識別、再開に必要な入力を耐久的に残す。秘密入力は平文保存せず、安全な参照または再入力の契約を使う。
2. 必要な外部処理を実行する。外部が対応するなら同じ操作IDで重複排除し、要求の意味と保持期間を共有する。
3. 応答が返らない場合は「未実行」と決めず、結果不明として照会する。照会も重複排除もできない相手には、一回だけの業務効果を保証できると表現しない。
4. 成功を確認したら、DB側の結果・進捗を保存する。その保存に失敗しても、同じ外部結果を照合して先へ進めるようにする。
5. 後処理・補償が必要なら、別の耐久作業として実行する。補償も失敗し得るため、要対応まで追跡する。

画像差し替えでは、元画像が使える状態を守りながら、新画像を準備し、新参照と旧画像の清掃予定をDBで確定し、その後に旧画像を消します。新画像準備後にDB確定しなかった場合も、暫定実体の識別・猶予付き走査などで回収できるようにします。

削除では、対象キーを削除完了まで保持し、現在参照中ではないことを再確認します。「すでに存在しない」と「通信失敗で確認できない」は区別します。quotaなどの精算も操作単位で一回にするか、DBを正本に照合・再計算できる構成にします。

### 監査とキャッシュは、三つの形に横断して接続する

必須証跡は、完了後にメモリへ投げるだけで済ませず、合意した受理時点に合う耐久記録を置きます。ただし、変更の差分取得と最終commitの確認は別です。外側transactionがある場合も含め、試行・成功・不明を区別します。診断用の任意記録まで一律に業務を止める設計にはしません。

cache、件数、画像URL、SignalRは正本と区別し、許容遅延と再同期を持たせます。APIが成功した直後に派生状態が反映済みである必要があるなら、その条件を明示して別に検証します。最終一致を選ぶ場合も、失敗後に再計算・再取得できることが必要です。

<a id="recovery"></a>

## 6. 途中で止まったときにどう回復するか

### 状態を「成功・失敗」だけにしない

すべての機能へ同じ状態enumを追加する提案ではありません。非同期・外部処理を持つ機能について、少なくとも次の意味を識別できるようにします。既存のjob状態等で足りる場合は再利用します。

| 状態の意味 | 残しておく情報と次の動き |
|---|---|
| 受理済み・未起動 | 操作と入力が保存済み。起動担当が再発見する。 |
| 実行中 | どの手順・担当・版かを識別。担当停止時に安全に引き継ぐ。 |
| 再試行待ち | 対象失敗、回数、次回、入力・進捗。成功済み効果を増やさず再実行。 |
| 結果照会中・不明 | 外部やcommit結果を照会。失敗と決めつけて再反映しない。 |
| 完了 | 必須の業務効果が確認済み。結果照会・同操作再送へ対応。 |
| 要対応 | 自動回復の上限、恒久エラー、入力不足など。担当へ警告し、再開・補償・訂正を選ぶ。 |

処理担当の期限切れ後は、古い担当が状態を確定できない検査が必要です。ただし、その検査だけでは外部への二重送信を止められないため、外部の安定した操作IDや結果照会と組み合わせます。

### 停止点ごとに、回復の材料が残る構成にする

| 停止点 | 目標構成での扱い |
|---|---|
| DB transactionのcommit前 | 全体をrollback。再送は保存済み操作の有無を確認して実行する。 |
| commitは成功したが応答が消えた | 操作記録・正本の結果を照会する。HTTPエラーだけを根拠に同じ加算を繰り返さない。 |
| 本体保存後、配送担当の実行前 | 本体と同時確定した起動記録から作業を再発見する。 |
| 受信側の保存後、配送完了記録前 | 受信側の重複防止により同じ効果を増やさず、配送の記録だけを収束させる。 |
| 外部成功後、DBの進捗保存前 | 同じ操作ID・外部照会から結果を確認する。対応できない外部は残る保証外を明記する。 |
| 初期構築の手順効果後、完了記録前 | 手順の効果を照合し、安全に再実行または完了を回復する。手順名のmarkerだけで判断しない。 |
| 自動retryの上限 | 入力・対象キーを消さず、要対応として警告・照会・再投入へつなぐ。 |
| backupからの復元後 | 業務データと操作・重複防止記録の差、外部側の効果を照合する。復元だけで一回性が戻るとしない。 |

### 記録を保持することも接続の一部

操作IDやInboxを早く消すと、古い要求・eventの再投入が新しい操作に見える場合があります。clientの再送期間、Outbox隔離期間、外部の重複記録、backup復元、削除保留をまとめて考え、保持期間と期限後の再処理契約を決めます。

記録を永久保存すれば解決するわけでもありません。個人情報の最小化、秘密情報を残さないこと、削除期限、復旧に必要な最小情報を両立させます。

<a id="selection"></a>

## 7. 機能ごとに必要な構成だけを選ぶ

「全部そろえる」は、各機能が必要保証を実現する選択肢を持つという意味です。全機能へ操作テーブル、Outbox、Inbox、Saga、分散ロックを一括追加する意味ではありません。

| 機能の性質 | 最小限に必要な構成 | 追加の判断 |
|---|---|---|
| 一つのDB状態保存 | 入力・認可・業務検査、保存結果確認、必要な制約。 | 後勝ち編集を許すか、応答喪失を正本照会で扱えるか。 |
| 複数のDB保存を一体にする | 同じcontext・接続のtransaction、全保存結果の確認。 | 外側transaction・provider・managerの内部保存。 |
| 加算・集合・階層 | 上記に加え、保護範囲に合う競合制御。加算再送には操作識別。 | 競合後の再計算、最低人数・非循環等の集合条件。 |
| 必須の非同期処理 | 業務と起動記録の耐久確定、配送、受信側の重複防止、進捗照会。 | 受理と完了、未起動発見、試行上限、tenant接続。 |
| 外部変更・不可逆削除 | 対象情報・進捗の耐久保持、外部識別・照会、補償・清掃。 | 相手の契約、結果不明、再利用中の実体、精算の一回性。 |
| 派生値・期間値 | 正本・測定時点・版、照合・再計算、必要な根拠値保存。 | 許容遅延、逆順、誤った既存値の訂正。 |
| 必須監査 | 合意した耐久受理、重複許容sink、commit成否、再投入・保持。 | 種別別の保証、記録不能時の業務応答、運用責任。 |

例えば、二要素認証の無効化は既存DB transactionの適用が中心です。期限延長はそれに操作識別と競合制御が必要です。テナント初期構築は、さらに耐久起動・手順再開・秘密入力の回復が必要です。同じ「書き込み機能」でも、必要な部品の組み合わせは違います。

<a id="roadmap"></a>

## 8. 今後の開発の進め方

### 既存資産を使い、保証の切れ目から埋める

1. **機能ごとに成功条件を決める。** 正本、守る条件、受理・完了、許容する遅延・欠損、対象構成を短い保証契約にする。
2. **既存部品の適用で直る箇所を直す。** 保存結果の検査、同じDB内のtransaction、発行元の一本化を小さく進める。
3. **共通部品の不足を承認の上で補強する。** tenant受信契約、Inboxの例外分類・保存境界、必要な担当権制御を独立した変更として扱う。
4. **必要な業務状態を追加する。** 操作識別、起動要求、進捗、清掃・照合をmodule内で実装する。共通基盤の完成を待って全作業を止めないが、保証の未達を隠さない。
5. **復旧と運用までつなぐ。** 照会、再投入、補償、保持、警告、担当と期限を整える。
6. **検証済みの方式をSkillsへ反映する。** 全機能へ同じ仕組みを要求せず、機能の性質から構成と受入試験を選ばせる。

この順序は設計の進め方であり、全体の業務優先度ではありません。例えば、月次請求の期間誤りや画像の不可逆削除は、共通frameworkの整備とは独立して優先修正できます。三モジュールの具体的な順序は[既存の修正計画](identity-multitenancy-auditing-remediation-plan.md#sequence)に従います。

### Skillsに求める出力

Skillsはコードを生成する前に、「この機能ではどの部品を、何のために組み合わせるか」を説明する形にします。

| 必須の説明 | 例 |
|---|---|
| 正しさと成功条件 | 正しい対象月の値、必要な初期権限、メール受理と実到達の違い。 |
| 選ぶ基本形と保存境界 | 同期DB内で完結、Outboxで引き継ぐ、外部結果を照会する。 |
| 重複・競合・順序 | 同じ要求、異なる加算、別行の集合条件、古いeventをどう扱うか。 |
| 停止後の回復 | 何を保存し、誰が発見し、何を再実行・照会・補償するか。 |
| 保証外・未決 | 外部が重複排除できない、専用DB未検証、保持期限が未決など。 |
| 試験・運用の完成条件 | 別接続競合、commit前後停止、外部成功後応答喪失、上限後の引継ぎ。 |

現状のhelperの存在だけで合格にせず、承認が必要な共有変更や重要な業務判断が未決なら、それを出力して確認を要求します。

<a id="acceptance"></a>

## 9. 何を確認すれば完成と言えるか

「必要なクラスを追加した」「テストが緑になった」だけでは、目標構成の完成とはしません。機能の保証契約に対応する観測結果で判断します。

| 確認する性質 | 最低限の観測 |
|---|---|
| 正確な値 | 期間・単位・時刻境界・精度が業務根拠と一致する。 |
| 保存境界 | 各保存間の失敗で、一緒に確定すべき値と起動記録が食い違わない。 |
| 同時実行 | 別接続から同期点付きで競合させ、異操作の効果を保ち、集合条件を破らない。 |
| 同一操作の再送 | 並行再送、応答喪失、再投入でも効果が増えず、異入力は区別する。 |
| 起動・再開 | processを止めても受理済み入力を再発見でき、必要な秘密入力も安全に扱える。 |
| 外部の結果不明 | 照会・重複排除または要対応へ進み、未実行と推測して再反映しない。 |
| 作用先・順序 | 共有DB・専用DB・複数担当・逆順配送で、正しい対象と最終状態へ収束する。 |
| 上限後・保持 | 自動処理が尽きても対象と進捗を失わず、認可された回復と保持・削除を実行できる。 |

既存の重点46件成功は、共通機構の限定した範囲の証拠です。本書の将来像全体、実PostgreSQL・Redis・外部ストレージの障害、全機能の受入条件を満たした証拠ではありません。

本番の実効設定、instance数、外部側の識別・照会契約、backup、監視・担当が未確認なら、その条件での保証は未確認として残します。必要な部品が存在すること、正しく接続したこと、実構成で検証したことの三つを区別して完成判定します。

## 根拠と関連資料

- [詳細調査結果](data-write-quality-investigation-results.md): 共通保証、保存順序、15観点の判定、失敗シナリオ、ソース参照。
- [調査報告書・解説版](data-write-quality-investigation-report.md): 機能別の説明と課題・修正パターンの類型。
- [Identity・Multitenancy・Auditingの修正計画](identity-multitenancy-auditing-remediation-plan.md): 具体的な修正・依存・移行・受入条件。
- [調査・評価計画](data-write-quality-investigation-plan.md): 保証・証拠・評価観点の定義。

本書の目標構成は設計案です。現状の参照は以下の対象コミットに固定し、新しい実装の存在を示すものとしては使いません。

[outbox-enlist]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs#L188-L218
[idempotency]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Web/Idempotency/IdempotencyEndpointFilter.cs#L75-L302
[outbox-claim]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs#L75-L180
[event-bus]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/InMemory/InMemoryEventBus.cs#L54-L137
[inbox-store]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs#L23-L64
[domain-events]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Persistence/Inteceptors/DomainEventsInterceptor.cs#L34-L74
[jobs-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Jobs/Extensions.cs#L15-L95
[tenant-scope]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventTenantScope.cs#L8-L42
