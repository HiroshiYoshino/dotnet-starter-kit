# 主要モジュールのデータ整合性レビュー

## 調査対象・前提

- 文書作成日: 2026-10-15
- 対象リポジトリ: `HiroshiYoshino/dotnet-starter-kit`
- 調査対象コミット: [`a967537526f3b769c7d268da3ed2a6920af65ce9`][review-revision]
- 対象範囲: 10モジュールの主要28処理。全エンドポイント・全分岐の網羅的な監査ではない。
- 調査方法: ソースコードと設定のレビュー。記載した競合・障害シナリオをすべて再現確認したものではない。
- 本文は上記コミット時点の評価であり、その後の修正状況は反映しない。

## 結論

多くの機能は、1回の `SaveChangesAsync` によってDB内の複数更新をまとめている。ただし、同時更新による取りこぼし、複数回の保存による部分成功、ストレージ・ジョブ・キャッシュとの不整合には、見直すべき箇所が残っている。

特に、残高・在庫・権限・ファイル削除のように、不整合を許容しにくい機能を優先するのが妥当である。一方、通常のCRUDや通知の既読化まで、一律に明示トランザクションへ変更する必要はない。

## 評価する保証範囲

| 観点 | 確認内容 |
|---|---|
| DB内の原子性 | 一機能の書き込みが1回の保存または同一トランザクションに収まるか。途中失敗で一部だけ確定しないか。 |
| 同時更新 | 読み取りから書き込みまでの競合を、条件付き更新・競合トークン・行ロック・分離レベル等で保護するか。 |
| 業務制約 | 一意性、関連行、残高・在庫、状態遷移、集合全体の不変条件をどこで保証するか。 |
| DB外の処理 | ストレージ、Redis、Hangfire、HTTP送信、SignalR等とDB更新が食い違った場合にどう扱うか。 |
| 冪等性・復旧 | 重複要求、再配送、途中停止、部分成功の後に、安全に再実行できるか。 |
| 派生情報 | キャッシュ、カウンタ、監査記録が業務データとずれた場合、回復可能か。 |

リレーショナルDBでは、通常、1回の `SaveChangesAsync` 内の書き込みは原子的である。しかし、その前の読み取りを含む排他制御、別の保存、別DbContext、Redis、ストレージ、HTTP送信などまでは保証しない。したがって、`BeginTransactionAsync` がないことだけを問題とは評価していない。

トランザクションの原子性と同時更新の正しさも別の保証である。明示トランザクションを追加しても、分離レベルや更新条件によっては、残高・在庫の取りこぼしや集合制約の競合は解消しない。

### 評価区分

| 評価 | 意味 |
|---|---|
| 優先見直し | 機能の性質上、不整合を避けるべき箇所。 |
| 改善推奨 | 競合・再試行・復旧を強化したい箇所。 |
| 仕様次第 | 最後の書き込み優先やbest-effortを許容するかなど、必要な保証水準による箇所。 |
| 概ね妥当 | 現在の保存単位が機能に適合している箇所。保証外の処理がないという意味ではない。 |

## 1. Identity・Billing

| モジュール／主要機能 | DB内の原子性・業務制約 | 同時更新の保証範囲 | DB外・再実行・復旧の保証範囲 | 評価・見直し方向 | 参照 |
|---|---|---|---|---|---|
| **Identity：ユーザー登録** | ユーザー作成、ロール・グループ付与、追加保存が別々。登録全体を一括で確定する境界はない。 | Identityのユーザー制約はあるが、登録全体の完了を保証するものではない。 | 確認メールのジョブ投入も別処理。登録イベントは2経路から生成され、Outboxとの原子性も不足。 | **優先見直し。** [#8][issue-8] の経路一本化に続き、必須DB更新とOutboxを原子化する。メール送信はコミット後に耐久的に起動する。 | [登録処理][identity-registration]／[追加保存・Outbox][identity-registration-outbox] |
| **Identity：パスワード変更** | パスワード変更、ドメインイベント用保存、変更日時更新、履歴登録が複数保存。途中失敗すると、新パスワードだけ有効になるなどの部分成功があり得る。 | Identityの競合検知はあるが、変更日時・履歴まで含む一体性はない。 | 変更日時更新の `IdentityResult` を確認していない。履歴の登録と古い履歴削除も別保存。 | **優先見直し。** パスワード・変更日時・必要な履歴を同じトランザクションに含め、全保存結果を確認する。履歴の掃除は別処理でもよい。 | [変更フロー][identity-password]／[変更日時][identity-password-expiry]／[履歴保存][identity-password-history] |
| **Identity：ユーザーロール・ロール権限変更** | ロール追加・削除を個別に保存。権限も、既存claimの削除と新規claimの追加が一括確定されない。ロール割当等で結果未確認の呼び出しがある。 | 「最低1人の管理者を残す」確認は変更前の件数取得。複数ユーザーへの並行変更をまとめて保護する排他はない。 | 権限キャッシュ無効化はDBと別処理。途中失敗で部分的な権限集合や古いキャッシュが残り得る。 | **優先見直し。** 一要求の変更集合を原子化し、`IdentityResult` を必ず確認する。管理者数のような集合制約には、トランザクションだけでなく直列化等も必要。 | [ロール割当・件数確認][identity-user-roles]／[権限更新][identity-role-permissions] |
| **Identity：グループと所属ロールの更新** | グループ本体と所属ロールの差分を1回で保存。関連テーブルのキー・FKで基本的な整合性を保つ。 | 管理者が並行して所属ロール集合を編集した場合、双方の意図を調停する集約バージョン確認はない。 | 権限キャッシュ無効化は保存後の別処理。DB成功後の無効化失敗は別途回復が必要。 | **仕様次第。** DB内の一括保存は妥当。編集競合を利用者に通知したいなら集約バージョンを追加し、キャッシュには期限や無効化再試行を設ける。 | [グループ更新][identity-group-update] |
| **Identity：プロフィール・画像変更** | 最終 `UpdateAsync` の結果は確認するが、電話番号変更は先に別保存され得る。その結果確認も不足。 | 任意の `If-Match` とIdentityの競合検知があり、競合を412に変換する設計は良い。 | 新画像アップロードはDB保存より前。最終保存失敗で新画像が孤立し得る。旧画像削除は最終更新成功後。 | **改善推奨。** 既存の競合検知は維持し、電話番号を含む更新の保存境界・結果確認を整える。画像には失敗時の補償または孤立オブジェクト清掃を用意する。 | [プロフィール更新][identity-profile] |
| **Identity：refresh tokenのローテーション** | ユーザー側refresh tokenとsession側hashが別保存。途中失敗で双方が不一致になり得る。 | ユーザー更新は旧token一致を条件にした更新ではない。並行ローテーションを単一の成功に絞る仕組みが不足。 | session更新対象が見つからなくても更新されずに終了する。監査も別処理。 | **優先見直し。** 同じDBのtoken・session更新を原子化し、旧token/hashを条件にしたCAS更新と更新件数確認を行う。 | [ローテーション順序][identity-refresh]／[ユーザー更新][identity-token-store]／[session更新][identity-session-refresh] |
| **Billing：入金確定・wallet残高加算** | 請求書、Topup要求、残高、台帳を1回で保存。同一Topupの台帳には一意制約がある。 | **異なるTopupが同じwalletを並行更新すると、残高の加算が取りこぼされ得る。** 台帳が2件保存されても残高には片方しか反映されない可能性。 | ここでのDB更新一括化自体は良い。同一要求の重複防止と、異なる要求の競合防止は別問題。 | **優先見直し。** 残高は原子的加算、行ロック、または競合検知＋再試行で保護する。金額を扱うため、単なる最後の書き込み優先は不適切。 | [入金一括保存][billing-payment]／[残高加算][billing-wallet-credit]／[Topup一意制約][billing-topup-unique] |
| **Billing：利用量集計・請求書生成** | 利用量snapshot保存と請求書保存は別。請求書本体と明細は一括保存。期間・用途等の一意制約がある。 | 同時生成の重複行はDBで防げるが、競合した要求を既存の生成結果へ合流させる扱いは不足。 | snapshotを先に保存して再利用する設計は、再開可能な処理として成立する。請求書とOutboxの分離は [#7][issue-7] の対象。 | **改善推奨。** 全処理を大きなトランザクションにする必要はない。snapshotの不変性・再利用を明確にし、一意制約競合時の再読込と請求書＋Outboxの原子化を行う。 | [請求書生成][billing-invoice-generation]／[snapshot保存][billing-usage-snapshot]／[請求書制約][billing-invoice-config] |
| **Billing：契約プラン切替・契約更新** | 既存Active契約の取消と新契約作成は一括保存。Active契約の一意制約がある。 | 二重ActiveはDBで防げるが、競合時にどちらの要求を採用するかは別問題。 | イベント処理で契約更新後に請求書を作成する経路は別保存。請求失敗後の再実行まで契約操作と一体ではない。 | **改善推奨。** 契約単体の保存は妥当。「契約変更と請求書作成を必ずセットにする」なら同一処理境界か、処理状態・イベントIDに基づく再開可能なフローを設ける。 | [プラン切替][billing-subscription-assign]／[イベント経由の契約更新][billing-subscription-maintenance] |

## 2. Multitenancy・Catalog・Tickets

| モジュール／主要機能 | DB内の原子性・業務制約 | 同時更新の保証範囲 | DB外・再実行・復旧の保証範囲 | 評価・見直し方向 | 参照 |
|---|---|---|---|---|---|
| **Multitenancy：テナント作成・provisioning開始** | provisioning本体と初期stepsは一括保存。ただし、テナント作成・開始記録・ジョブ投入全体は一体ではない。 | 実行中確認はチェック後作成。テナント単位の実行権獲得を保証する一意制約・CASは見当たらない。 | Pending保存後、Hangfire投入前に停止すると開始されない記録が残り得る。DB作成・migration・seedも段階別の外部処理。 | **優先見直し。** 耐久的な起動要求、テナント単位のlease、滞留Pendingの回復、各stepの冪等化が必要。全DBをまたぐ巨大トランザクションは適さない。 | [開始記録とenqueue][tenant-provisioning-start]／[step実行][tenant-provisioning-job]／[DB設定][tenant-provisioning-config] |
| **Multitenancy：契約期間の延長** | 現在の期限から延長後の期限を計算して保存する。 | 並行延長は同じ旧期限を読み、片方の延長を取りこぼし得る。 | テナントキャッシュ更新・Outboxは別処理。リトライが「同じ延長要求」なのか「追加延長」なのかを区別するキーも重要。 | **優先見直し。** 加算型の契約操作なので、競合検知＋再計算や直列化で保護する。課金と連動するなら要求IDによる重複防止も必要。 | [期限計算・保存][tenant-renewal] |
| **Multitenancy：テーマ・ロゴ変更** | テーマのDB更新は1回の保存。 | テーマ編集のETag等はない。最後の編集優先を許容するかは仕様による。 | **旧ロゴ等をDB保存前に削除する。** 保存失敗すると、DBが参照する旧画像だけ消えた状態になり得る。 | **優先見直し。** 新画像の準備→DB確定→旧画像削除へ順序を変更する。削除再試行と新画像の孤立清掃を設ける。 | [更新と保存の順序][tenant-theme] |
| **Catalog：商品在庫の増減** | ドメインで減算後の負数を拒否し、1回で保存する。 | 在庫は読み取り後にメモリ上で増減。並行調整の取りこぼしを防ぐ条件付きSQL・競合トークン等はない。 | 在庫数の保存後に外部処理を挟む問題よりも、DB内の並行更新が中心的な課題。 | **優先見直し。** 実在庫として使うなら、負数条件を含む原子的SQL更新、行ロック、または競合検知＋再試行が必要。 | [在庫操作][catalog-stock-domain]／[保存][catalog-stock-handler] |
| **Catalog：商品作成・識別子の重複防止・復元** | 商品は単一保存。SKU・Slugには有効行を対象とする一意制約があり、重複をDB側で防ぐ。 | 事前重複確認だけでなくDB制約が最終防衛になる。並行作成・復元では制約競合が起き得る。 | 通常のCRUDに対して、追加の大きなトランザクションは必須ではない。 | **概ね妥当。** 一意制約を維持する。競合を利用者向けの409等に整えることは推奨するが、保存方式全体の再設計は不要。 | [商品作成][catalog-product-create]／[商品制約][catalog-product-config] |
| **Catalog：商品画像追加・サムネイル・並び替え** | 親商品と画像集合の変更を一括保存。サムネイルはドメイン上で1つに保つ。 | 「サムネイル1つ」はDB制約ではなく集約内の保証。並行追加・選択では集合の不変条件が破れる可能性がある。 | ファイルの実体とURLはFiles側との別境界。ここでの画像集合保存だけでは実体まで保証しない。 | **改善推奨。** 親商品を含む集約の競合検知・直列化を追加する。単純に部分一意indexを足すと降格・昇格の更新順序で失敗するため、既存設計への配慮が必要。 | [画像集合操作][catalog-product-images]／[一意indexを設けない理由][catalog-image-config] |
| **Catalog：カテゴリ階層の変更** | 保存前に祖先をたどり、自己参照・循環を検出する。単一要求内の検査は適切。 | AをBの子にする変更と、BをAの子にする変更が並行すると、双方の事前検査が通る可能性。別行同士なので個別行のversionだけでは不十分。 | 特別な外部処理はなく、読み取りと階層更新の競合が主な問題。 | **改善推奨。** 常にツリーであることが要件なら、同一ツリーの変更を直列化するか、Serializable＋再試行等で祖先読み取りまで保護する。 | [循環検査と保存][catalog-category-update] |
| **Tickets：担当変更・解決・再開・コメント追加** | 状態遷移をドメインで検査し、不正遷移は409。コメントと親更新日時も一括保存する。 | 検査は読み取った状態に対するもの。複数担当者による並行操作の上書きは防いでいない。 | ドメインイベントはローカル処理。通常のチケット操作に分散トランザクションは不要。 | **仕様次第。** 保存単位と状態機械は妥当。並行担当者の操作を黙って上書きしてはいけないなら、ticketのversion／ETagを導入する。 | [状態機械][tickets-state-machine]／[コメント一括保存][tickets-comment] |

## 3. Chat・Files・Webhooks・Notifications・Auditing

| モジュール／主要機能 | DB内の原子性・業務制約 | 同時更新の保証範囲 | DB外・再実行・復旧の保証範囲 | 評価・見直し方向 | 参照 |
|---|---|---|---|---|---|
| **Chat：メッセージ・返信の投稿／削除** | 投稿本体・添付・mention・親返信数・channel時刻を一括保存する。 | 返信数の増減は読み取り後更新。並行投稿・削除で件数がずれ得る。最終投稿時刻も古い書き込みで後退し得る。 | 保存後にSignalR通知、その後mentionイベントを直接発行。通知失敗で、DB保存済みなのに要求が失敗扱いになる可能性。 | **改善推奨。** カウンタを原子的更新または再計算可能な派生値にする。リアルタイム通知は再取得で回復できればよいが、mentionの耐久性が必要なら別途保証する。 | [投稿の保存・通知][chat-send]／[返信数の増減][chat-reply-count]／[削除時の保存][chat-delete] |
| **Chat：1対1 DMのfind-or-create** | 利用者ペアから決定的な `DirectKey` を作り、一意indexで同じDMの重複を防止。 | 同時作成では片方のINSERTが制約違反になる。既存DMの取得に合流する処理は不足。 | 保存後の通知は別処理だが、DMの一意性自体はDBで保護されている。 | **改善推奨。** 制約違反時に同じキーのDMを再取得して返す。設計自体を変える必要はない。 | [find-or-create][chat-dm]／[一意制約][chat-channel-config] |
| **Chat：既読位置更新** | 対象メッセージが同じchannelにあることを確認し、既読位置を保存する。 | 既読位置は単純代入。別タブの遅い要求が、新しい既読位置を古い位置に戻し得る。 | SignalR通知は保存後。再取得で表示回復は可能だが、DBの位置後退は残る。 | **改善推奨。** 通常の既読機能なら、メッセージ順序に基づく条件付き更新で単調に進める。意図的な「未読に戻す」は別操作に分ける。 | [既読更新・通知][chat-mark-read]／[位置の代入][chat-member-read] |
| **Files：アップロード要求・finalize** | Pending状態を確認し、実サイズ・型・scan結果を検査して状態を保存する。 | Pending確認に実行権獲得のCAS等はない。並行finalizeでquotaを二重加算し得る。要求時のquota確認は予約ではない。 | quota加算→DB保存→Outbox追加の順で別処理。DB失敗でquotaだけ増える可能性。 | **優先見直し。** finalizeの実行権獲得、file単位の冪等なquota精算、失敗後の照合を設ける。DB状態＋Outboxの原子化だけではRedisとの不整合は解消しない。 | [quota確認とPending保存][files-upload-request]／[finalize順序][files-finalize] |
| **Files：公開範囲変更・storage key移動** | 明示トランザクションと行ロックを使用し、参照先変更＋Outboxをコミットする。 | 対象fileの同時移動と旧key参照確認をロックで保護する。 | コピー後にDB確定し、旧object削除を試みる。イベントによる削除再試行がある。ただし、DB失敗後の新コピーは孤立し得る。 | **概ね妥当。** 外部ストレージをDB rollback対象にしない設計は適切。孤立コピーの清掃と再試行枯渇時の運用を補う余地はある。 | [移動・コミット][files-relocate]／[参照確認と削除][files-relocate-cleanup] |
| **Files：削除済み・期限切れファイルのpurge** | DBの物理削除自体は単一SQLだが、候補取得から処理完了までのclaimはない。 | 並行purgeが同じ候補を処理し、削除済みfileのquota返却を二重に実行し得る。 | object削除失敗を記録してもDB行を削除する。quota返却も行削除後で、失敗すると次回の候補がなくなる。期限切れpurgeにも同様のobject清掃漏れがある。 | **優先見直し。** file単位の削除作業記録・lease・冪等な返却を設ける。object削除・quota精算が完了するまで、再試行に必要な情報を残す。 | [削除済みpurge][files-purge-deleted]／[期限切れpurge][files-purge-orphaned] |
| **Webhooks：イベントのsubscription別fan-out** | 対象subscriptionを読み、配信ジョブを個別投入。配信予定の全件をDBで一括確定する構成ではない。 | event＋subscription単位の耐久的な重複防止がないため、部分成功後の再試行では重複投入が問題になる。 | enqueue失敗をsubscriptionごとに捕捉して終了する。呼び出し元には成功として戻り、失敗分の再試行が保証されない。 | **優先見直し。** event＋subscriptionをキーにした配信予定を耐久化し、未投入分を再実行する。単に例外を再送出するだけでは成功済み分の重複を解消できない。 | [fan-outと失敗処理][webhooks-fanout] |
| **Webhooks：HTTP配信・配信結果記録** | HTTP送信後に試行結果をDB保存する。各試行は記録できるが、相手の受信とDB保存は原子化できない。 | 試行ごとに新しいdelivery IDが生成されるため、試行IDだけでは同じ論理配信の重複排除に使えない。 | 相手の受信成功後、結果保存前に失敗すると再送され得る。transient失敗には有限回の再試行がある。 | **改善推奨。** at-least-onceを前提に、安定した論理配信IDと試行IDを分離する。元event ID等による受信側の冪等化と、再試行枯渇後の再送運用を明確にする。 | [HTTP送信・結果保存・再試行][webhooks-dispatch] |
| **Notifications：mention通知の作成** | 通知1件を保存後、SignalR送信。通知には元event IDに基づく一意キーがない。 | 同じeventの再処理でも新しい通知IDを作成する。 | 通知保存後、SignalR失敗やInbox記録前の停止が起きると、再処理時に通知が重複し得る。Chat側の直接発行も耐久配送ではない。 | **改善推奨。** 「1ユーザー・1eventにつき1通知」が要件なら、source event ID＋user ID等の一意キーを設ける。SignalRはDB通知の再取得で回復できるようにする。 | [通知保存とpush][notifications-mention]／[handler後のInbox記録][eventing-inbox-order] |
| **Notifications：通知の既読化** | 全件既読はuserと未読条件付きの単一 `ExecuteUpdateAsync`。個別既読も既読日時を未設定時だけ設定する。 | 重複要求でも既読という状態は保たれる。個別既読時刻の厳密な先着順までは保証しない。 | 必須の外部更新を伴わない。 | **概ね妥当。** 通常の通知UIなら追加トランザクションは不要。最初の既読時刻の厳密性が必要な場合だけ、個別更新も条件付きSQLにする。 | [全件既読][notifications-mark-all-read]／[個別既読のドメイン操作][notifications-domain] |
| **Auditing：変更監査・非同期監査保存** | tenantごとに一括保存するが、業務DB保存と監査保存は別。EntityChangeは `SavingChanges` 時点に発行するため、コミット成功の証跡とは一致しない。 | 複数tenantのbatchが途中まで成功した後に全体再試行すると、保存済みIDの重複で再失敗し得る。 | 通常laneは過負荷時に古い項目を破棄。security laneもプロセス停止への耐久性はない。保存再試行・DLQはある。 | **仕様次第。ただし再試行の冪等化は改善推奨。** best-effortログなら許容できる。監査欠損不可・コミット済み変更だけを証明する要件なら、耐久的な搬送と成功／試行の区別が必要。 | [キューの保証][auditing-channel]／[tenant別保存][auditing-sql-sink]／[保存前の監査発行][auditing-interceptor]／[保存再試行][auditing-worker] |

## 4. 修正手段の使い分けと既存Issueとの関係

| 課題の種類 | 主な対策 | 注意点 |
|---|---|---|
| 同一DBの複数保存による部分成功 | 保存の集約、同一トランザクション、保存結果の確認 | `UserManager` 等が内部で保存する経路も含めて境界を設計する。 |
| 残高・在庫・件数・期限の更新取りこぼし | 条件付きSQL、原子的加算、行ロック、競合検知＋再試行 | トランザクション追加だけでは十分とは限らない。再試行では古い計算結果を使い回さない。 |
| 管理者数・カテゴリ階層・画像集合等の不変条件 | 集約バージョン、集合・階層単位の直列化、適切な分離レベル | 別行を変更する要求同士の競合は、個別行のversionだけでは防げないことがある。 |
| 業務DB更新とOutboxの分離 | 同一トランザクションへの参加、対応する接続・DbContextの共有 | 接続先・接続共有の条件も確認する。トランザクションを開始するだけで別コンテキストが自動参加するとは限らない。 |
| Redis・ストレージ・ジョブ・HTTPとの不一致 | 耐久的な作業記録、冪等化、補償、照合・清掃、滞留処理の回復 | DB rollbackで外部効果は戻らない。成功済み部分を識別して安全に再開する。 |
| キャッシュ・SignalR等の派生情報 | 有効期限、無効化再試行、DBからの再取得、再計算 | 即時一致が必要か、最終的な一致でよいかを仕様として明確にする。 |
| 監査の完全性 | 耐久的な搬送、重複許容保存、コミット成功と試行の区別 | best-effortログと欠損不可の監査証跡は、異なる保証水準である。 |

既存Issueの [#7][issue-7] は業務DB更新とOutboxの原子性、[#8][issue-8] は登録イベントの二重生成、[#9][issue-9] はintegration-event skillの原子的保存例に関する課題である。

この3件は、実装の依存関係と手戻りを避ける観点では、#8の経路一本化→#7の原子性確保→#9の実装例更新の順が妥当である。この順序は、本レビュー全体の業務影響の大きさによる優先順位とは別の判断である。

本表の残高・在庫の競合、quota精算、purge、ジョブ起動、HTTP配送、監査等の課題は、Outbox原子化だけでは解消しない。それぞれの機能に必要な保証範囲を決め、対応する仕組みを選ぶ必要がある。

## 参照リンク

ソース参照はすべて調査対象コミットに固定している。Issueリンクは更新され得るため、最新の対応状況はIssue側を確認する。

[review-revision]: https://github.com/HiroshiYoshino/dotnet-starter-kit/tree/a967537526f3b769c7d268da3ed2a6920af65ce9
[issue-7]: https://github.com/HiroshiYoshino/dotnet-starter-kit/issues/7
[issue-8]: https://github.com/HiroshiYoshino/dotnet-starter-kit/issues/8
[issue-9]: https://github.com/HiroshiYoshino/dotnet-starter-kit/issues/9
[identity-registration]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs#L33-L69
[identity-registration-outbox]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs#L319-L339
[identity-password]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserPasswordService.cs#L94-L117
[identity-password-expiry]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/PasswordExpiryService.cs#L75-L84
[identity-password-history]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/PasswordHistoryService.cs#L66-L109
[identity-user-roles]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRoleService.cs#L113-L166
[identity-role-permissions]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Roles/RoleService.cs#L164-L242
[identity-group-update]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Groups/UpdateGroup/UpdateGroupCommandHandler.cs#L39-L121
[identity-profile]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserProfileService.cs#L84-L185
[identity-refresh]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Tokens/RefreshToken/RefreshTokenCommandHandler.cs#L94-L111
[identity-token-store]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/IdentityService.cs#L113-L134
[identity-session-refresh]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/SessionService.cs#L327-L354
[billing-payment]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L235-L277
[billing-wallet-credit]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Domain/Wallet.cs#L34-L55
[billing-topup-unique]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/WalletTransactionConfiguration.cs#L29-L34
[billing-invoice-generation]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L44-L112
[billing-usage-snapshot]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/UsageReporter.cs#L34-L65
[billing-invoice-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/InvoiceConfiguration.cs#L29-L46
[billing-subscription-assign]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Features/v1/Subscriptions/AssignSubscription/AssignSubscriptionCommandHandler.cs#L28-L43
[billing-subscription-maintenance]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/IntegrationEventHandlers/TenantSubscriptionMaintenance.cs#L14-L51
[tenant-provisioning-start]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningService.cs#L35-L71
[tenant-provisioning-job]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningJob.cs#L34-L102
[tenant-provisioning-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Data/Configurations/TenantProvisioningConfiguration.cs#L10-L18
[tenant-renewal]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantService.cs#L200-L223
[tenant-theme]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantThemeService.cs#L114-L180
[catalog-stock-domain]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Domain/Product.cs#L139-L154
[catalog-stock-handler]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Features/v1/Products/AdjustProductStock/AdjustProductStockCommandHandler.cs#L17-L32
[catalog-product-create]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Features/v1/Products/CreateProduct/CreateProductCommandHandler.cs
[catalog-product-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Data/Configurations/ProductConfiguration.cs#L17-L44
[catalog-product-images]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Domain/Product.cs#L156-L237
[catalog-image-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Data/Configurations/ProductImageConfiguration.cs#L25-L32
[catalog-category-update]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Features/v1/Categories/UpdateCategory/UpdateCategoryCommandHandler.cs#L22-L64
[tickets-state-machine]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Tickets/Modules.Tickets/Domain/Ticket.cs#L88-L190
[tickets-comment]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Tickets/Modules.Tickets/Features/v1/Tickets/AddTicketComment/AddTicketCommentCommandHandler.cs#L30-L39
[chat-send]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Messages/SendMessage/SendMessageCommandHandler.cs#L79-L100
[chat-reply-count]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Domain/Message.cs#L156-L158
[chat-delete]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Messages/DeleteMessage/DeleteMessageCommandHandler.cs#L40-L59
[chat-dm]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Channels/FindOrCreateDm/FindOrCreateDmCommandHandler.cs#L33-L59
[chat-channel-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Data/Configurations/ChatChannelConfiguration.cs#L20-L29
[chat-mark-read]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Channels/MarkChannelRead/MarkChannelReadCommandHandler.cs#L31-L59
[chat-member-read]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Domain/ChannelMember.cs#L28
[files-upload-request]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Features/v1/RequestUploadUrl/RequestUploadUrlCommandHandler.cs#L69-L110
[files-finalize]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Features/v1/FinalizeUpload/FinalizeUploadCommandHandler.cs#L36-L103
[files-relocate]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Services/FileStorageRelocator.cs#L65-L119
[files-relocate-cleanup]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Services/FileStorageRelocator.cs#L128-L170
[files-purge-deleted]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Jobs/PurgeDeletedFilesJob.cs#L67-L113
[files-purge-orphaned]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Jobs/PurgeOrphanedFilesJob.cs#L63-L99
[webhooks-fanout]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Webhooks/Modules.Webhooks/Services/WebhookFanoutHandler.cs#L80-L103
[webhooks-dispatch]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Webhooks/Modules.Webhooks/Services/WebhookDispatchJob.cs#L96-L165
[notifications-mention]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/MentionedInChannelIntegrationEventHandler.cs#L46-L78
[eventing-inbox-order]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/InMemory/InMemoryEventBus.cs#L118-L128
[notifications-mark-all-read]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/Features/v1/MarkAllNotificationsRead/MarkAllNotificationsReadCommandHandler.cs#L22-L29
[notifications-domain]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/Domain/Notification.cs
[auditing-channel]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Core/ChannelAuditPublisher.cs#L42-L95
[auditing-sql-sink]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/SqlAuditSink.cs#L23-L71
[auditing-interceptor]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/AuditingSaveChangesInterceptor.cs#L21-L84
[auditing-worker]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Core/AuditBackgroundWorker.cs#L147-L205
