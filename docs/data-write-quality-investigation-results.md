# データ書き込み品質の調査結果

- 調査日: 2026-10-15
- 重点テスト追試: 2026-10-15 12:10 JST（46件成功、失敗・スキップ0件）
- 対象: `HiroshiYoshino/dotnet-starter-kit`
- 対象コミット: `a967537526f3b769c7d268da3ed2a6920af65ce9`
- 実施計画: [データ書き込み品質の調査・評価計画](data-write-quality-investigation-plan.md)
- 成果物の範囲: ソース・設定・既存テストの調査、重点検証の試行、評価、改善・Skills仕様案。アプリケーション、migration、Skills、運用設定は変更していない。

## 1. 結論と証拠の読み方

**単一保存の原子性、一意制約、状態遷移の検査、Outboxの配送再試行などは存在する。しかし、それらを組み合わせても、機能全体の重複防止・正確性・完了性が成立しない経路が残っている。**

特に重要なのは、残高・在庫・期限の加算取りこぼし、対象期間と異なる利用量のsnapshot、業務保存とOutbox・job起動の間の停止、consumerの業務効果とInbox記録の間の停止、外部削除失敗後の回復情報消失である。単にリトライを増やすと、二重反映や外部送信が増える場合もある。

調査は、102件のcommand handlerと、サービス・consumer・job等の追加候補を39個の機能グループへ整理した。うち33グループは代表経路を詳細分析し、6グループは棚卸しにとどめた。**グループ内の全commandを同じ深さで検証したという意味ではない。** 詳細経路と未着手の派生経路を以下に区別する。

初回は依存関係の復元で重点テストを実行できなかったが、利用者によるNuGet監査対応後の追試で、選択したFramework.Testsの46件がすべて成功した。成功範囲は第10節に示す既存テストに限る。Dockerを使うPostgreSQL・Redis・storageの隔離障害試験は未実施であり、以下の業務上の反例分析を実DBで再現済みとするものではない。

| 証拠区分 | 本文での扱い |
|---|---|
| D: 明示された仕様・意図 | domainの状態ガード、コメント、設定、制約に表れた意図。ただしコメントだけで保証成立とはしない。 |
| H: 必要保証の業務仮説 | 「異なる入金は残高へ両方反映」「既読は後退しない」「確定通知は失わない」等。未承認の条件として記録する。 |
| S: 実装・設定 | 保存順序、例外処理、制約、再実行分岐など、対象コミットで確認した事実。 |
| T: 既存テスト | 試験コードから確認した対象・条件。存在するだけでは実行成功の証拠としない。 |
| V: 今回の動的検証 | 初回の実行阻害と、依存更新後の重点46件成功。実行条件・件数・確認範囲は第10節に記録する。 |

「不適合」は、表の必要保証に対してソース上の反例経路があるという意味である。Hに依存する判定は、採用する業務要件を確認して確定する。並行実行・停止点の説明は、Sに基づく分析であり、再現済みと読み替えない。

## 2. 基準点・環境・適用条件

| 項目 | 確認結果と制限 |
|---|---|
| コード基準点 | 業務実装の静的調査と参照リンクは上記SHAに固定。追試は同じHEADに利用者の未コミット依存更新を加えた作業ツリーで実行したため、元コミットそのままの依存構成での成功とはしない。 |
| 検証環境 | Windows、.NET SDK `10.0.401`。実行中の本番アプリケーションや実利用者データは対象外。 |
| DB | APIの基本設定はPostgreSQL。[共通provider設定][db-provider]ではPostgreSQLの自動実行再試行を有効化していない。MSSQLでは有効化している。 |
| tenant構成 | 共有DBのtenant filterと、tenantの接続文字列による専用DBの双方を想定した実装。[BaseDbContext][base-db]参照。Billingなど明示的に横断データを扱う経路は個別のcaller条件が必要。 |
| eventing | `EventingOptions`の既定値はInMemory、Inbox有効、hosted dispatcher有効。実環境のoverrideは不明。[設定][event-options] |
| storage・quota | 基本設定はlocal storage、quota無効。Redis quota有効時の評価は別条件として記載。[基本設定][base-config] |
| instance数・実効設定 | 本番のinstance数、Redis共有範囲、job server数、DI差し替え、環境変数、RabbitMQ topologyは未確認。ソースの既定値を本番の値としていない。 |
| データ保持 | backup、復元試験、PITR、Redis永続化、DLQファイルの永続volume、外部受信側の保持期間は未確認。DB保存成功だけでこれらを保証しない。 |
| 既存の作業ファイル | 計画、先行レビュー、別の実装計画を保持。graphifyの既存出力を変更していない。 |
| 追試時の依存構成 | 利用者が`src\Directory.Packages.props`と`src\BuildingBlocks\Persistence\Persistence.csproj`を更新。EF Core等の.NET 10系依存とSystem.Security.Cryptography.Xml/Pkcsは`10.0.13`、PersistenceにPkcs参照追加。アシスタントはこの変更を編集せずに追試した。 |
| 動的検証の制限 | 初回の`NU1902`・`NU1903`による復元阻害は、依存更新後の重点実行では発生せず、46件成功。Dockerに依存する試験は追試対象外で、初回のDocker shim起動不可も今回再確認していない。監査抑制オプションは追加していない。 |

以下で「単一SaveChangesは原子的」とするのは、対象relational providerでその保存が成功し、同じ保存内の行がまとめて確定する範囲である。先行する読み取り、別の保存、別DB、Redis、blob、HTTP、メール、job起動まで含むものではない。

## 3. 書き込み経路の棚卸し

### 3.1 command handlerの対応

件数はruntimeプロジェクトの`ICommandHandler<...>`実装を探索した結果であり、endpoint数や業務機能数ではない。名称は機能名で示し、共通の`Command`等のsuffixを省略している。

| module | 件数 | 候補と機能ID |
|---|---:|---|
| Identity | 32 | Users: Register・ConfirmEmail・AdminConfirmEmail・ResendConfirmation → I01、ChangePassword・ResetPassword・ForgotPassword → I02、AssignRoles → I03、SetProfileImage・Update → I05、Delete・ToggleStatus → I08。Roles: Upsert・Delete・UpdatePermissions → I03。Groups: Create・Update・Delete・AddUsers・RemoveUser → I04。Tokens: Generate・Refresh、Sessions: Revoke・RevokeAll・AdminRevoke・AdminRevokeAll → I06。TwoFactor: Enroll・VerifyEnroll・Disable → I07。Impersonation: Start・End・RevokeGrant → I09。 |
| Billing | 11 | Wallets: CreateTopupRequest・ApproveTopupRequest・RejectTopupRequest、Invoices: MarkInvoicePaid → B01。GenerateInvoices・CaptureUsageSnapshots → B02。AssignSubscription → B03。IssueInvoice・VoidInvoice → B04。CreatePlan・UpdatePlan → B05。 |
| Multitenancy | 7 | CreateTenant・RetryProvisioning → T01。AdjustValidity・RenewTenant → T02。UpdateTheme・ResetTheme → T04。ChangeActivation → T05。 |
| Catalog | 18 | AdjustStock → C01。AddImage・RemoveImage・ReorderImages・SetThumbnail → C02。Category: Create・Update・Delete・Restore → C03。Brand: Create・Update・Delete・Restore、Product: Create・Update・Delete・Restore・ChangePrice → C04。 |
| Tickets | 9 | Create・Update・Delete・Restore・Assign・Resolve・Close・Reopen・AddComment → K01。 |
| Chat | 15 | Send・Edit・Delete・Pin・UnpinMessage → H01。FindOrCreateDm・AddChannelMembers・RemoveChannelMember・AddReaction・RemoveReaction → H02。MarkChannelRead → H03。Create・Update・Archive・RestoreChannel → H04。 |
| Files | 5 | RequestUploadUrl・FinalizeUpload → F01。ChangeFileVisibility → F02。DeleteFile・RestoreFile → F05。 |
| Webhooks | 3 | TestSubscription → W01。CreateSubscription・DeleteSubscription → W02。 |
| Notifications | 2 | MarkRead・MarkAllRead → N01。 |
| Auditing | 0 | command handlerではなくinterceptor・publisher・worker・sink・保守jobから書き込む。A01・A02へ分類。 |
| 合計 | **102** | 全候補を上記IDへ割り当てた。 |

HTTP methodだけでは棚卸しできない。[ConfirmEmail][confirm-email]はGETからcommandを実行してユーザー状態を変更する。また、登録serviceは外部認証からも呼ばれる。

### 3.2 command以外の候補

機械探索では53ファイルが候補となった。これは53個の書き込み機能という意味ではなく、read-onlyや登録処理も含む。さらに探索で漏れる委譲job・domain handlerを手動で追加した。

| module | 候補の分類・対応 |
|---|---|
| Auditing | AuditBackgroundWorker・SqlAuditSink → A01、AuditRetentionJob → A02。AuditingModule・Infrastructure.Hosting.ServiceCollectionExtensionsは登録経路として参照し、業務機能の評価から除外。 |
| Billing | BillingService → B01・B02・B04、UsageReporter → B02、TenantSubscribed/TenantRenewed handler・TenantSubscriptionMaintenance → B03。BillingDbInitializerは初期化として詳細評価外。追加: MonthlyInvoiceJob → B02。 |
| Catalog | FileStorageKeyChangedProductImageHandler → C02・F02。 |
| Files | FileStorageRelocator・DeleteSupersededObjectHandler → F02、PurgeDeletedFilesJob・PurgeOrphanedFilesJob → F03、MigrateLegacyPublicFileKeysJob → F04。 |
| Identity | UserRegistrationService・UserRegisteredEmailHandler → I01、UserPasswordService・PasswordExpiryService・PasswordHistoryService → I02、RoleService・UserRoleService・UserPermissionService → I03、UserProfileService・FileStorageKeyChangedAvatarHandler → I05・F02、IdentityService・SessionService・SessionCleanupHostedService・TokenGeneratedLogHandler → I06、UserStatusService → I08、ImpersonationGrantService → I09。RolePermissionSyncer・RolePermissionSyncHostedService・IdentityDbInitializerは初期化・同期として詳細評価外。追加: UserRegisteredEventHandler → I01。 |
| Multitenancy | TenantProvisioningService・TenantProvisioningJob → T01、TenantService → T01・T02・T05、TenantExpiryScanJob → T03、TenantThemeService → T04。TenantStoreDrainTargetProviderは配送先列挙で共通機構へ分類。GetTenantMigrationsQueryHandler・TenantMigrationsHealthCheckは状態参照であり、業務書き込み候補から除外。 |
| Notifications | MentionedInChannelIntegrationEventHandler → N01、InvoiceIssuedEmailHandler・TenantEnteredGraceEmailHandler・TenantExpiredEmailHandler・TenantNearingExpiryEmailHandler → N02。 |
| Webhooks | WebhookDeliveryService・WebhookDispatchJob・WebhookFanoutHandler → W01。WebhooksModuleは登録経路として参照し、業務機能としては除外。 |

追加の境界として、共通のOutbox/Inbox保存、quota更新、監査搬送を第4節で扱う。DbMigrator・seed・起動時permission syncそのものの品質、CLI、フロントエンドの再送policy、インフラのbackup運用は詳細調査外である。

この棚卸しは静的探索で見つかった経路の分類であり、reflectionによる任意の呼び出しや全環境の差し替え実装の網羅を証明しない。未着手経路は「安全と確認済み」ではない。

## 4. 共通機構の保証と再実行範囲

| ID・機構 | 実装・再実行主体・単位 | 保証する範囲／保証外 |
|---|---|---|
| G01 保存・transaction | 一保存のrelational transaction。明示transactionは代表経路ではFileStorageRelocatorにある。Outboxは同じ接続のambient transactionへ参加する。[provider][db-provider]・[Outbox参加][outbox-enlist] | 複数保存を自動で一つにする共通wrapperは確認できない。Outboxの参加機構があっても、呼び出し元にtransactionがなければ独立保存。 |
| G02 DB実行再試行 | 共通PostgreSQL設定は`EnableRetryOnFailure`なし。MSSQLは有効。[設定][db-provider] | `CreateExecutionStrategy()`を呼ぶだけでPostgreSQLの障害再試行が有効になるわけではない。実行戦略による再試行と、業務再読込・再計算・結果不明の照合は別。 |
| G03 HTTP要求の冪等性 | endpointへのfilter付与とrequest headerの双方が必要。既定response TTL 24時間、reservation 1分。tenant・caller・method・route・route値・利用者キーで分離。[filter][idempotency]・[設定][idempotency-options] | 29 endpointファイルに付与を確認。全102 handlerへの適用ではない。Redisがないreservationはprocess内。lease更新なし。本文hash照合は確認できず、同じキー・異なる入力を拒否する保証はない。 |
| G04 response replayの限界 | 成功結果をhandler後に保存。client切断tokenをhandlerから切り離す。cache保存失敗はlogしてfail-open。[filter][idempotency] | commit後・response記録前の停止、長時間実行によるlease失効、TTL後の再送、cache障害は業務重複防止の保証外。2xxのみcacheするため、部分成功後に非2xxとなった要求も再実行され得る。 |
| G05 Outbox起動・再試行 | event ID・payloadを保存してからdispatcherが処理。既定batch 100、poll 10秒、lease 300秒。失敗backoffは30秒起点の指数増加、上限3600秒。[store][outbox-store]・[dispatcher][outbox-dispatch]・[設定][event-options] | `RetryCount + 1 >= 5`でdead-letterとなる既定実装。通常の連続失敗は初回を含め5回であり「初回+5回」としない。保存前のevent欠落は配送リトライでは回復しない。 |
| G06 Outbox競合・終端 | PostgreSQLのclaimは`FOR UPDATE SKIP LOCKED`。非PostgreSQLはclaimなし読み取りでsingle instanceを警告。[claim・更新][outbox-claim] | lease後の旧worker継続に対する、所有者比較による確定・fencingは確認できない。dead-letter照会・redriveのstore APIはあるが、moduleの公開操作・通知・対応SLOは未確認。 |
| G07 event busの構成差 | 既定InMemoryでは同processのhandlerを実行。RabbitMQ publisherはpersistent message・durable exchange・publish retryを使う。[InMemory][event-bus]・[RabbitMQ][rabbit-bus] | InMemoryのhandler完了と、RabbitMQへのpublish完了は意味が異なる。broker routing、publisher confirmation、consumer retry、外部consumerの永続性まで含む保証は今回未確認。 |
| G08 Inbox | `{eventId, handlerName}`を照会し、handlerの完了後に処理済みを保存。[呼出順][event-bus]・[Inbox][inbox-store] | handlerのDB保存／メール／HTTPとmarkerは非原子的。並行deliveryと保存後停止で効果が重複し得る。またmarker保存の全`DbUpdateException`を競合insertとして抑制し、実際のduplicate制約か確認していない。 |
| G09 local domain event | `SavedChangesAsync`でeventをclearしてpublish。非キャンセル例外はlogして抑制。[interceptor][domain-events] | 通常の暗黙transactionでは保存後。明示transaction内では最終commit前の場合もある。耐久キューではなく、消費失敗時の自動再配送はない。「domain handlerだから業務更新と原子的」とは言えない。 |
| G10 Hangfire | PostgreSQL/MSSQLのjob storage、queue・worker設定がある。[登録][jobs-config] | jobがenqueue済みなら永続起動を利用できる。業務保存→enqueueの隙間は別問題。共通登録には一般jobのretry回数の明示設定を確認できず、ライブラリ既定を実効運用保証としない。 |
| G11 outbound HTTP | `.AddHeroResilience`を付けたclientに適用。既定retry 3回、初回delay 1秒、total timeout 30秒、attempt timeout 10秒。[既定値][http-options]・[登録][http-retry]・[Webhooks登録][webhook-module] | 全IOへ自動適用されない。Webhooksではjob最大5実行とclient最大4試行が重なり、policy上は最大20 HTTP試行相当。timeout・breaker・対象失敗により少なくなる。fan-outによる別job重複はさらに別。 |
| G12 監査・運用 | in-memory channel、SQL sink、失敗batchのfile DLQ、metrics/log。[channel][audit-channel]・[worker][audit-worker]・[DLQ][audit-dlq] | log/metricsの存在は、alarm、担当者、復旧手順の運用を証明しない。primary sinkは1batchにつき合計3試行、待機150ms・300ms。DLQ保存自体の失敗はlogのみで、成功を保証しない。 |
| G13 tenant routing | drain scopeは接続文字列を設定。一方consumer dispatch scopeはtenant IDだけを設定してからhandlerを解決する。[drain][drain-scope]・[consumer scope][tenant-scope]・[BaseDbContext][base-db] | **専用DB構成での適用上のギャップ。** consumer用scopeが完全なtenant情報を持たず、tenant接続へ切り替えられない経路がある。行filterのID一致と、正しいDBへ書いたことは別。共有DBでは同じ問題とは限らない。実DB検証は未実施。 |

G08の広いcatchは、今回読んだ既存Inboxテストの「同じmarkerの再登録」では否定できない。接続・制約・保存等の別の`DbUpdateException`も正常終了扱いとなり得るため、duplicateだけを判別する必要がある。

G13は特に、Catalog画像・Identity avatar・Notifications・Webhook subscription読取のようなtenant contextを必要とするconsumerで確認すべき事項である。独立したdrain scopeが正しいOutbox DBを選んでも、そのことだけではconsumerのDB選択が保証されない。Billingの横断データ設計に同じ指摘を無条件に適用しない。

## 5. 対象・必要保証一覧

各行は簡易な保証契約である。必要保証はD/Hで出所を示す。全行共通で、実運用の保持期限、許容遅延、alarm・対応責任は、別途確認がない限り未決または未確認である。

| ID | 機能・入口／詳細範囲 | 成功・正本・必要保証 | 状態 |
|---|---|---|---|
| I01 | 登録service、外部認証登録、登録domain handler。確認・再送は棚卸し。 | 正本はuser・role/group membership。H: 登録完了に必要な初期権限と起動記録がそろう。D/H: 一登録から同義eventを二重生成しない。 | 詳細 |
| I02 | パスワード変更・reset service、期限・履歴。ForgotPasswordは起動経路の確認。 | password hashが正本。H: policy有効時は期限・履歴も成立。旧passwordでの再送を復旧手段としない。 | 詳細 |
| I03 | userのrole割当、role permission集合更新。role名CRUDは棚卸し。 | membership・claimが正本。D: 最低一管理者。H: 成功時のpermission集合が要求と一致し、失敗で中間集合を残さない。 | 詳細 |
| I04 | group更新・ユーザー追加。create/delete/removeの個別経路は棚卸し。 | group・関連行が正本。H: 集合更新は一まとまり、重複memberは作らない。上書き許容とcache遅延は未決。 | 詳細 |
| I05 | profile更新service、avatar URL consumer。SetProfileImage専用入口は棚卸し。 | userのprofile・blob参照。D: If-Match時は古いprofileを拒否。H: DB参照先を更新失敗で失わない。 | 詳細 |
| I06 | refresh handler・user token保存・session token更新。login・revoke・cleanupは一部確認／棚卸し。 | user tokenとsessionが正本。H: rotation結果が一致し、一旧tokenの競合rotationが説明可能。session欠如を許す互換仕様はD。 | 詳細 |
| I07 | 2FA VerifyEnroll・Disable handler。Enroll全体は棚卸し。 | Identity user state/keyが正本。H: 成功応答は設定保存成功。disableとkey resetの部分成功を扱う。 | 詳細 |
| I08 | ToggleStatus・Delete共通service。 | user IsActiveが正本。D: 自己無効化・管理者無効化を拒否し、保存失敗を返す。H: 監査の成功定義は別途合意。 | 詳細 |
| I09 | impersonation grantの終了・取消service。Startのtoken発行全体は棚卸し。 | grant行が正本。H: 終端状態が判定cacheへ反映され、再実行が反映失敗を修復できる。 | 詳細 |
| B01 | top-up承認、請求支払、wallet・台帳。申請・却下は棚卸し。 | invoice・request・wallet・ledger。D: 支払時は一保存でcredit＋状態変更。H: 別々の入金を双方反映し、残高と台帳を一致させる。 | 詳細 |
| B02 | 利用量snapshot・期間請求、MonthlyInvoiceJob。 | 期間別測定値・invoiceが正本。D/H: 指定期間の利用量を請求し、同tenant/period/purposeを重複発行しない。 | 詳細 |
| B03 | subscription割当、TenantSubscribed/TenantRenewed consumer。 | active subscription・契約invoice。D: activeは高々一つ。H: lifecycle event再送で不要な契約置換を繰り返さない。 | 詳細 |
| B04 | IssueInvoice・VoidInvoice service。 | invoice stateが正本。D: domainで状態検査して保存。H: 競合した支払・取消の優先、通知必須性は未決。 | 詳細 |
| B05 | plan create/update。 | 金額・quota・planの管理。精度、既存契約との関係、編集競合を含む詳細契約は未確認。 | 棚卸しのみ |
| T01 | tenant作成からprovisioning開始・job・手動retry。 | tenant catalogとprovisioning/steps。H: 受理後に完了／失敗へ到達、未enqueueを発見。Completed stepの意味は要確認。 | 詳細 |
| T02 | 期限更新・renew service、後続event。 | ValidUpto・planが正本。D: 有効な残期間に積み増し。H: 異なる更新操作を取りこぼさない。operator直接補正は別操作。 | 詳細 |
| T03 | TenantExpiryScanJob。 | notice記録とOutbox。D: tenant/type/validity単位の通知抑止。H: 記録済みを「起動要求も耐久化済み」と対応付ける。 | 詳細 |
| T04 | theme更新のblob差替。reset全体は棚卸し。 | theme行とlogo/favicon参照。H: DB保存失敗でも旧参照先を失わない。cacheは最終一致を選べる。 | 詳細 |
| T05 | activation変更。 | provisioningとactivationの連携。個別の同時実行・再開保証は未確認。 | 棚卸しのみ |
| C01 | stock調整handler・domain。 | Stockが正本。D: 負在庫拒否。H: 異なるdeltaを両方反映、同じ論理調整は一回だけ反映。 | 詳細 |
| C02 | 商品画像集合domain・URL移動consumer。 | ProductImage集合とFileAsset参照。D: thumbnailの一意性。H: 競合・逆順eventでも最新参照へ収束。 | 詳細 |
| C03 | category親変更の祖先検査。create/delete/restoreは棚卸し。 | 親関係が正本。D: cycleを拒否。H: 同時に別行を更新しても非循環を保つ。 | 詳細 |
| C04 | brand/product CRUD・価格・復元。 | SKU/slug制約等は参照したが、全関連FK・復元衝突・価格精度まで追跡していない。 | 棚卸しのみ |
| K01 | ticketの状態domain・comment保存。delete/restore個別経路は棚卸し。 | ticketとcomments。D: 逐次状態遷移。H: 競合編集・コメント重複・閉鎖との競合をどこまで拒否するかは未決。 | 詳細 |
| H01 | message送信・削除、返信件数・mention起動。edit/pin/unpinは一部domain確認。 | message・関連行。H: reply countが重複削除で減らない。SignalR欠落と通知欠落を別々に判断。 | 詳細 |
| H02 | 二人DM find/create、member・reactionのdomain/制約。個別membership handler全体は棚卸し。 | channel・membership・reaction。D: 二人DM、member、reactionの重複行防止。group DMは毎回作成というDあり。 | 詳細 |
| H03 | MarkChannelRead handler・member。 | LastReadMessageId。H: 古い要求で既読を後退させない。意図的mark-unreadを提供するかは未決。 | 詳細 |
| H04 | channel create/update/archive/restore。 | channel state・membership。個別の復元競合、編集条件は未確認。 | 棚卸しのみ |
| F01 | upload request→object upload→finalize。 | FileAsset・object、quotaは派生／精算値。D: HEAD/size/type検査。H: finalize・quota debit・event起動を一論理操作として扱う。 | 詳細 |
| F02 | visibility変更・FileStorageRelocator・旧object削除・URL consumer。 | FileAsset storage keyが正本。H: pointerと起動記録を同時確定、旧objectを安全に削除、参照consumerを最新へ収束。 | 詳細 |
| F03 | deleted/orphan purge job。 | 対象行・object・quota精算。H: 削除を確認するまで回復情報を保持し、返金は一回。不可逆処理。 | 詳細 |
| F04 | legacy public key migration job。 | FileAsset key。D: 失敗件を残し、成功件は再移動しない。登録済みjobの再実行で残りを処理。 | 詳細 |
| F05 | file delete/restore。 | soft-delete stateと外部参照。復元期限・利用中参照との関係は未確認。 | 棚卸しのみ |
| W01 | event fan-out、TestSubscription、dispatch job・HTTP。 | 元event、subscription、delivery attempt。H: 必須配送の欠落を検出。受信側が重複排除する契約と成功確認が必要。 | 詳細 |
| W02 | subscription create/delete。 | destination・secret・event集合。テストURL等の値検査全体と削除後のdelivery方針は詳細評価外。 | 棚卸しのみ |
| N01 | mention consumer・通知既読。 | notification行。H: 一source event／recipientから通知を重複生成しない。既読は再実行しても未読へ戻さない。 | 詳細 |
| N02 | invoice／期限メールconsumer・共通sender。 | 送信効果と配送記録の必要性。D: 現状はbest-effort。H: 必須到達か任意通知かを決める。 | 詳細 |
| A01 | audit interceptor→channel→worker→SQL/DLQ。 | audit envelope ID。D: lane別の欠損・待機policy。H: 欠損不可の証跡なら耐久受理とcommit成否の区別が必要。 | 詳細 |
| A02 | audit retention job。 | audit rowと種類別cutoff。H: 合意した保持期間・削除期限・holdを守る。legal hold等は未確認。 | 詳細 |

## 6. 実装・retry・復旧一覧

「要求再送」は利用者/clientによる再実行であり、自動再試行が実装されているという意味ではない。記載しない独自業務リトライは確認できていない。DB共通設定はG02、endpoint単位のresponse replayはG03/G04を適用条件つきで参照する。

| ID | 保存・競合境界と操作識別 | retry・restart・枯渇後／実効保証・証拠 |
|---|---|---|
| I01 | user作成→role追加→group保存→email enqueue→domain event保存→明示Outbox。domain handlerも別IDで同義eventをOutboxへ追加。 | user重複検査は後続の欠落補完ではない。途中成功後の再登録は既存user／duplicateで止まり得る。enqueue済みemail jobと保存済みOutboxだけが各基盤のretry対象。[登録][reg]・[初期化/Outbox][reg-tail]・[domain handler][reg-event] |
| I02 | managerのpassword保存→event用保存→expiry manager保存→history追加保存→history prune保存。expiryのIdentityResultは未確認のまま捨てる。 | 旧password再送は失敗し、同じ変更の後続だけを再開するoperation記録は確認できない。reset経路は変更経路と同じexpiry/history処理を呼んでいない。[password][password]・[expiry][password-expiry]・[history][password-history] |
| I03 | Add/RemoveRoleはループ内保存、結果の一部を未確認。管理者数は事前query。permission削除はmanagerごと、追加は後続保存、cacheはその後。 | 別userの同時admin解除は個別user stampだけで集合条件を守れない。集合を再要求しても部分成功・cache更新失敗の終端照会がない。[user roles][user-roles]・[permissions][role-perms] |
| I04 | group属性とrole差分は一保存。member追加もまとめて保存、その後cache無効化。 | 逐次追加は既存memberを除外。競合追加や集合全体の編集には再読込・競合結果の扱いが必要。cache失敗後の要求再送で同じ無効化が再度必要か、専用の回復経路は未確認。[更新][group-update]・[追加][group-add] |
| I05 | 任意If-Match＋Identity stamp。blob upload後、phone manager保存と最終UpdateAsyncは別。旧blob削除は最終保存後。 | 412では再取得・再編集できるが、先のmanager保存も含む全体rollbackではない。新blob孤立、保存後削除・cacheの未完了は別回復が必要。[profile][profile] |
| I06 | user token更新→session hash更新→audit。sessionは該当なしで正常終了し得る。 | rotationのold token比較と全体更新が一つのCASになっていない。結果不明で古いtokenを再送しても同じ成功結果を取り出す契約は確認できない。[refresh][refresh]・[user token保存][token-store]・[session][sessions] |
| I07 | `SetTwoFactorEnabledAsync`、Disableではさらに`ResetAuthenticatorKeyAsync`。双方のIdentityResultを確認せずtrueを返す。 | 保存失敗を成功扱いし得る。enable・key resetは複数保存であり、handler全体の完了状態や補修記録はない。[有効化][twofactor-on]・[無効化][twofactor-off] |
| I08 | manager一更新、IdentityResult確認、その後audit。管理者の無効化自体を拒否。 | user state保存の失敗は表面化する。「最後の管理者を同時に無効化する」問題をこの経路に無条件適用しない。監査欠損の扱いはA01に依存。[status][status] |
| I09 | grant保存→cache status設定。終了／取消済みなら次回は早期return。 | DB成功・cache失敗後の再送は、terminal判定でcache設定まで到達せず修復しない。TTLまたはDB再読込の実効条件に依存。[grant][grant] |
| B01 | 支払ではinvoice・request・wallet・ledgerを一保存。Topup ReferenceIdのunique。承認はinvoice/request保存後にOutbox。 | 同じtop-up creditは制約で重複防止するが、異なるtop-upのwallet read-modify-writeは取りこぼし得る。承認済みrequestはPendingでなく、再承認で欠落Outboxを補完しない。[支払/承認][billing-pay]・[wallet][wallet]・[台帳制約][ledger-unique] |
| B02 | snapshotを先に保存、invoiceとlinesは後の一保存。[period/resource][usage-config]、[period/purpose][invoice-config]に一意性。 | 既存snapshot/invoiceは再利用する。**指定periodをquota読取へ渡さず現在値を読む。** MonthlyInvoiceJobは前月指定だがRedis ApiCalls keyは現在月、前月keyは月境界失効。月次全tenant処理は個別例外をlogして継続。[請求][billing-gen]・[snapshot][usage]・[月次job][monthly]・[Redis][quota-period] |
| B03 | active取消＋作成は一保存、active一意制約。lifecycle consumerからinvoice作成は別処理。 | Extendは単調延長の意図。Replaceはsource event完了markerと別なので、再配送で再置換し得る。Outbox/Inboxによる配送と業務冪等性を分ける。[置換/延長][subscription-maint]・[制約][subscription-config] |
| B04 | domain状態変更→一保存。caller tenantまたはroot条件で対象invoiceを絞る。 | IssueInvoice service自体はstate保存だけで、明示Outbox追加はない。発行を必須通知の成功まで含めるかは未決。競合状態遷移はversion等の追加確認が必要。[発行][invoice-issue]・[取消/対象選択][invoice-void] |
| T01 | provisioning＋steps保存→job enqueue→JobId保存。correlation IDでjobが同じrecordを使う。 | automatic rerunはCompleted stepをskip。手動RetryはStartAsyncで**新record・新correlation**。保存後enqueue前のPending残留は、再開始がPending/Running判定で拒否され得る。job失敗は記録してrethrow。jobの最終retry回数は未確認。[service][provision]・[job][provision-job]・[mapping][provision-config] |
| T02 | ValidUptoをread→AddMonths→tenant store更新→cache refresh→呼出側Outbox。 | 異なるrenewの期間積み増しを直列化する制御は確認できない。同じrenewを再送するoperation IDもなく、再送は新しい積み増しになり得る。[renew][renew]・[呼出側event][renew-event] |
| T03 | notice不存在確認→notice保存→tenant設定→Outbox追加。 | notice存在で次回scanをskip。Outbox失敗をtenant単位でlogし、jobにはrethrowしない。次の定期scanも欠落eventを作らない。[expiry scan][expiry-scan] |
| T04 | 新blob upload→旧blob削除→theme保存→cache無効化。 | DB失敗後に旧URLのまま旧objectだけなくなる。DB再試行では消したbytesは復元できない。[theme][theme] |
| C01 | Stockをread→delta加算→一保存。専用version／条件付き加算は確認できない。 | 要求再送は同じdeltaをもう一度適用。異なるdeltaの同時実行は一方を取りこぼす経路がある。domainの負値ガードはこの競合を防がない。[handler][stock-handler]・[domain][stock-domain]・[mapping][product-config] |
| C02 | 画像差分は同じ保存。thumbnail単一化はdomain、DB一意制約は設けない設計。 | 空集合への同時追加・同時thumbnail変更は集約競合が必要。URL consumerは旧key一致時のみrewriteするため逐次重複に強いが、逆順eventを永久skipする可能性がある。[画像][images]・[mapping][image-config]・[URL解決][url-event]・[consumer][image-consumer] |
| C03 | 親chainをqueryしてcycle判定→親変更→一保存。 | 別行A/Bを同時に互いの親へ変更すると、各検査時点ではcycleがなくても両保存後にcycleになり得る。個別row tokenだけでも十分でない。[category][category] |
| K01 | 状態ガードと関連commentを一保存。閉鎖済みの再Close等はno-op。 | 逐次の状態検査はある。別要求の検査後に状態が変わる競合を止めるversionは確認できない。commentは新規行、API replayはG03/G04の条件付き。[domain][ticket]・[comment][ticket-comment] |
| H01 | message/attachment/mentionと親reply count/channel時刻を一保存→SignalR→mention publish。 | message IDは毎回生成。DeletedAtUtcはquery filterのsoft-deleteではない。SoftDeleteがno-opでもhandlerが親countを再減算する。SignalR後続失敗後の再送もDB効果を戻さない。[送信][chat-send]・[削除][chat-delete]・[domain][message] |
| H02 | 二人DM deterministic key、member/reaction unique。作成後SignalR。 | 制約は二重行を止めるが、同時find/createの敗者が既存IDへ収束する再読込はない。reaction逐次追加はno-op。group DMの毎回新規作成は二人DMと別仕様。[DM][dm]・[DM制約][channel-config]・[member制約][member-config]・[reaction制約][reaction-config] |
| H03 | 対象messageのchannel所属を検査→LastReadMessageIdへ無条件代入→保存→SignalR。 | 古い要求・retryでwatermarkが後退する。比較付き更新や最大値保持を確認できない。[handler][chat-read]・[member][member] |
| F01 | quota checkは予約でない。finalizeはPending確認→HEAD/scan→quota Record→DB保存→Outbox。 | 同時finalizeをCASにしていない。DB失敗でquotaだけ加算、Available保存後の再送は409で欠落eventを補修しない。request URLには独自TTLのAPI replayがある。[finalize][finalize]・[request][upload-request] |
| F02 | PostgreSQL row lock＋transactionでFileAsset pointerとOutboxを保存、copyはその前。旧object削除はcommit後。 | strategy単位は全transactionだがG02に注意。旧key削除は即時試行＋event再実行。copy成功後rollbackで孤立新objectが残り得る。非PostgreSQLはrow lockなしsingle-writer前提。URL consumerの順序問題は別。[relocator][relocate]・[cleanup][relocate-delete]・[URL解決][url-event]・[avatar consumer][avatar-consumer] |
| F03 | storage削除をbest-effort実行→対象行ExecuteDelete→quota返金。最終削除条件は選定済みIDで、削除状態・Pending状態・期限を再検査しない。 | deleted job: retry 2回、300/1800秒。orphan job: 3回、30/120/600秒。ただしfile/tenantエラーをcatchして正常終了する経路はretry対象にならない。行削除後は同じ候補を再発見できない。並行jobの二重返金や、候補選定後のrestore/finalizeとの競合も考慮が必要。[deleted][purge-deleted]・[orphan][purge-orphan] |
| F04 | tenant・page単位scope、keyset走査、各fileはrelocator。失敗は数え、最後にthrow。 | retry 3回、60/300/1800秒。次回は先頭から未移動keyを検索し、既に移動済みは対象外。個別catchがjob成功に化けない点はF03と異なる。登録済みjob＋PostgreSQL条件で再開性の根拠がある。[migration job][legacy-job] |
| W01 | subscriptionごとにenqueue、dispatchはHTTP→attempt保存。job入力にtenant/subscription/payloadを保持。 | enqueue例外はfan-outで抑制し、Inboxが完了し得る。dispatchはtransientをretry、恒久4xxはattempt失敗を保存してreturn。job retry 4回後Failed。HTTP成功後DB失敗なら外部重複。attemptのdelivery IDは毎回新規、payloadの元event IDは安定。[fan-out][fanout]・[dispatch][dispatch] |
| N01 | mention consumerは新規notification保存→SignalR→Inbox。mark-allは未読限定の単一ExecuteUpdate。 | 既読化自体は再実行に強い。生成はsource event uniqueがなく、保存後停止／SignalR失敗で新しいnotificationを重複生成し得る。[生成][mention]・[既読][notify-read] |
| N02 | consumerはtenant/emailを解決してsend。BillingEmailSenderは例外をlogして抑制。 | メール失敗でもhandler成功となりInboxが完了し得る。逆に送信成功後marker前停止では再送し得る。sender自身の耐久retryはなく、各mail provider内部policyは未確認。[consumer][invoice-mail]・[sender][mail-sender] |
| A01 | SavingChangesでdiffをpublish。既定laneはDropOldest、security laneはWaitだが両方memory。SQLはtenantごと保存。 | batch再試行は同じIDを使うが、途中tenant成功後の全batch再実行では成功済みPKで失敗し、後続tenantへ進めない可能性。3試行後file DLQ。停止時batch欠損を許す実装。SavingChangesの記録はcommit済み変更の証明ではない。[interceptor][audit-interceptor]・[sink][audit-sink]・[worker][audit-worker] |
| A02 | tenantを設定し、種類/cutoff単位で小batchのExecuteDeleteを反復。 | 同条件の再実行は削除済み行を対象にしない。tenant例外はlogして継続、次の定期実行で残りを拾える前提。Enabled、保持日数、legal hold、削除期限の運用契約は未決。[retention][audit-retention] |

棚卸しのみのB05・T05・C04・H04・F05・W02は、再試行・結果不明・回復の端から端までを未確認とする。共通機構が存在するという理由で、その機能の完了性を認定しない。

## 7. 15観点の状態

横幅を抑えるため5観点ずつ分ける。記号は **適=適合、不=不適合、要=要件未決、未=未確認、外=非該当**。機能別の適合は第5・6節に限定したS根拠による条件付き判定である。追試のVは第10節の共通機構テストに限り、機能全体の保証認定や未確認項目の一括解除には用いない。

同一グループ内で保証の異なる部分は、第6節と第8節の説明を優先する。例えばB01の支払保存自体は原子的だが、承認後Outboxまで含めたQ03は不適合である。多くのQ13/Q14/Q15を未確認にしているのは、backup・実環境・運用の情報がないためである。

### 7.1 正確性・入力・保存・競合・不変条件

| ID | Q01 正本・正確性 | Q02 値の妥当性 | Q03 原子性・完全性 | Q04 同時更新 | Q05 不変条件・参照 |
|---|---|---|---|---|---|
| I01 | 未 | 未 | 不 | 未 | 未 |
| I02 | 未 | 未 | 不 | 未 | 要 |
| I03 | 不 | 未 | 不 | 不 | 不 |
| I04 | 未 | 未 | 適 | 要 | 未 |
| I05 | 未 | 未 | 不 | 適 | 未 |
| I06 | 未 | 未 | 不 | 不 | 要 |
| I07 | 不 | 未 | 不 | 未 | 不 |
| I08 | 適 | 未 | 適 | 要 | 適 |
| I09 | 適 | 未 | 適 | 未 | 未 |
| B01 | 不 | 要 | 不 | 不 | 適 |
| B02 | 不 | 要 | 要 | 未 | 適 |
| B03 | 未 | 未 | 不 | 未 | 適 |
| B04 | 未 | 未 | 適 | 要 | 未 |
| T01 | 未 | 未 | 不 | 不 | 未 |
| T02 | 不 | 未 | 不 | 不 | 未 |
| T03 | 不 | 未 | 不 | 未 | 未 |
| T04 | 未 | 未 | 適 | 要 | 未 |
| C01 | 不 | 未 | 適 | 不 | 適 |
| C02 | 未 | 未 | 適 | 不 | 不 |
| C03 | 未 | 未 | 適 | 不 | 不 |
| K01 | 未 | 未 | 適 | 要 | 要 |
| H01 | 不 | 未 | 適 | 不 | 未 |
| H02 | 未 | 未 | 適 | 要 | 適 |
| H03 | 不 | 未 | 適 | 不 | 適 |
| F01 | 不 | 適 | 不 | 不 | 要 |
| F02 | 適 | 未 | 適 | 適 | 適 |
| F03 | 不 | 未 | 不 | 不 | 未 |
| F04 | 適 | 未 | 適 | 適 | 未 |
| W01 | 未 | 未 | 不 | 未 | 未 |
| N01 | 未 | 未 | 適 | 未 | 未 |
| N02 | 未 | 未 | 要 | 未 | 要 |
| A01 | 不 | 未 | 要 | 未 | 未 |
| A02 | 未 | 要 | 適 | 未 | 要 |

Q02の「未」はvalidatorがないという指摘ではなく、範囲・通貨・精度・UTC・overflow等の全境界値の契約を確認できていないという意味である。B01/B02ではdecimalと通貨検査の根拠はあるが、DBの小数精度と業務上の通貨別丸め契約の一致は未決。F01の「適」は読んだHEADのsize/type検査範囲に限る。C01のQ05は逐次負在庫拒否に限り、加算取りこぼしはQ04で扱う。

### 7.2 重複・順序・再試行・再開・外部効果

| ID | Q06 冪等性・識別 | Q07 順序・鮮度 | Q08 リトライ | Q09 耐久起動・回復 | Q10 外部効果・補償 |
|---|---|---|---|---|---|
| I01 | 不 | 未 | 要 | 不 | 不 |
| I02 | 未 | 未 | 要 | 不 | 要 |
| I03 | 未 | 未 | 要 | 未 | 要 |
| I04 | 未 | 要 | 要 | 未 | 要 |
| I05 | 未 | 適 | 要 | 未 | 不 |
| I06 | 不 | 未 | 要 | 未 | 要 |
| I07 | 未 | 未 | 要 | 未 | 外 |
| I08 | 適 | 要 | 要 | 外 | 要 |
| I09 | 適 | 未 | 要 | 不 | 不 |
| B01 | 不 | 未 | 要 | 不 | 不 |
| B02 | 適 | 不 | 要 | 未 | 要 |
| B03 | 不 | 未 | 要 | 未 | 要 |
| B04 | 未 | 要 | 要 | 外 | 要 |
| T01 | 不 | 未 | 要 | 不 | 要 |
| T02 | 不 | 不 | 要 | 不 | 要 |
| T03 | 適 | 未 | 不 | 不 | 不 |
| T04 | 未 | 要 | 要 | 未 | 不 |
| C01 | 不 | 未 | 要 | 外 | 外 |
| C02 | 未 | 不 | 要 | 未 | 要 |
| C03 | 未 | 要 | 要 | 外 | 外 |
| K01 | 未 | 要 | 要 | 外 | 外 |
| H01 | 不 | 不 | 要 | 不 | 要 |
| H02 | 未 | 未 | 要 | 未 | 要 |
| H03 | 適 | 不 | 要 | 外 | 要 |
| F01 | 不 | 未 | 要 | 不 | 不 |
| F02 | 適 | 不 | 要 | 適 | 要 |
| F03 | 不 | 未 | 不 | 不 | 不 |
| F04 | 適 | 未 | 適 | 適 | 要 |
| W01 | 不 | 要 | 適 | 不 | 不 |
| N01 | 不 | 未 | 要 | 未 | 要 |
| N02 | 要 | 要 | 要 | 要 | 要 |
| A01 | 不 | 未 | 適 | 不 | 要 |
| A02 | 適 | 外 | 要 | 適 | 外 |

Q09の「外」は、詳細対象の同期DB更新について別の受理済み非同期作業を必要保証としていない場合。そのDB commit後の応答喪失はQ06で評価する。Q10の「外」は必須のDB外業務効果を詳細対象に含めない場合であり、共通監査はA01で評価する。A02のQ07はcutoff以下の集合を削除する処理で、event配送順序の要件がない。

Q08の「要」はretryが必要か／どの失敗をどこで再実行するかの要件が未決である場合。retry未実装を一律に不適合としていない。W01の適合はenqueue済みjobのtransient配送retry、F04は失敗集約後throwによるjob retry、A01はsinkの有限試行に限定する。枯渇後の回復全体まで適合とはしない。

F02/F04の競合・再開の適合は、PostgreSQL、接続共有・transaction参加の正しい登録、job/eventの耐久保存済みという前提。非PostgreSQLやconsumer DB選択には別の保証外がある。F02/C02の順序不適合は第9節の逆順consumerシナリオに対する判定である。

### 7.3 派生値・追跡・保持・構成・運用

| ID | Q11 派生値・最終一致 | Q12 追跡・監査・訂正 | Q13 耐久性・保持・削除 | Q14 分離・構成 | Q15 検出・運用 |
|---|---|---|---|---|---|
| I01 | 要 | 未 | 未 | 未 | 未 |
| I02 | 不 | 未 | 未 | 未 | 未 |
| I03 | 要 | 未 | 未 | 未 | 未 |
| I04 | 要 | 未 | 未 | 未 | 未 |
| I05 | 要 | 未 | 未 | 未 | 未 |
| I06 | 不 | 未 | 未 | 未 | 未 |
| I07 | 未 | 未 | 未 | 未 | 未 |
| I08 | 要 | 要 | 未 | 未 | 未 |
| I09 | 不 | 未 | 未 | 未 | 未 |
| B01 | 不 | 適 | 未 | 未 | 未 |
| B02 | 不 | 未 | 未 | 未 | 未 |
| B03 | 未 | 未 | 未 | 未 | 未 |
| B04 | 未 | 未 | 未 | 未 | 未 |
| T01 | 未 | 適 | 未 | 未 | 未 |
| T02 | 要 | 未 | 未 | 未 | 未 |
| T03 | 不 | 未 | 未 | 未 | 未 |
| T04 | 要 | 未 | 未 | 未 | 未 |
| C01 | 外 | 未 | 未 | 未 | 未 |
| C02 | 不 | 未 | 未 | 未 | 未 |
| C03 | 外 | 未 | 未 | 未 | 未 |
| K01 | 未 | 未 | 未 | 未 | 未 |
| H01 | 不 | 未 | 未 | 未 | 未 |
| H02 | 要 | 未 | 未 | 未 | 未 |
| H03 | 要 | 未 | 未 | 未 | 未 |
| F01 | 不 | 未 | 未 | 未 | 未 |
| F02 | 不 | 適 | 未 | 未 | 未 |
| F03 | 不 | 不 | 不 | 未 | 未 |
| F04 | 要 | 未 | 未 | 未 | 未 |
| W01 | 未 | 適 | 未 | 未 | 未 |
| N01 | 要 | 未 | 未 | 未 | 未 |
| N02 | 未 | 未 | 未 | 未 | 未 |
| A01 | 外 | 不 | 未 | 未 | 未 |
| A02 | 外 | 未 | 要 | 未 | 未 |

Q11の「外」は、対象経路で別のcache/counter等を必要保証として追跡していない場合。Stock自体の算術整合性はQ01/Q04、categoryの親関係はQ05、audit搬送の欠損はQ09/Q12で扱う。B01の追跡適合は台帳のReferenceId、T01はcorrelationとstep記録、F02はeventのold/new key、W01はattempt履歴に限定する。

Q14は実環境を未確認としているが、G13の専用DB consumer経路にはSによる構成依存の不一致がある。これは未確認を理由に無視するという意味ではなく、共有／専用DBごとに追加判定すべき共通課題である。

棚卸しのみの6グループは**Q01〜Q15すべて未確認、総合評価は判定保留**とする。非該当の判断もまだ行っていない。

## 8. 評価・改善一覧

優先欄の4文字は順に**業務影響／発生しやすさ／検出の難しさ／復旧の難しさ**で、高・中・低を示す。実測確率ではなく、必要条件と失われる情報からの評価である。証拠の確かさは別列とし、以下の機能別の反例経路自体は動的再現していない。重点46件の成功でこれらの評価を変更していない。

| ID | 根拠観点・差分 | 総合評価／優先4軸 | 改善方向・受入条件 | 証拠・未決 |
|---|---|---|---|---|
| I01 | Q03/Q06/Q09: 部分登録・二重event・enqueue隙間 | 優先見直し／高中高中 | 明示publisherへ一本化。初期権限と必要な作業記録を同transaction、既存userから未完了再開。全停止点で一登録・一論理event。 | S高。登録完了の必須項目はH確認。 |
| I02 | Q03/Q09/Q11: passwordだけ確定、後続修復不能 | 優先見直し／高中高中 | 期限・履歴の必要policyごとに保存境界を集約、全IdentityResultを確認。変更成功後の不足状態を旧passwordなしで補修。 | S高。history/expiry有効条件確認。 |
| I03 | Q03/Q04/Q05: 中間権限集合・集合条件の競合 | 優先見直し／高高高中 | 全保存をまとめ、管理者数は集合単位で制御。2管理者の同時解除を別接続で拒否／直列化。cache回復を別管理。 | S高、競合は分析。権限反映遅延未決。 |
| I04 | Q04/Q11: 集合編集競合・cache不足 | 仕様次第／中中中中 | overwrite許容を決める。必要ならgroup version、duplicate制約エラーの再読込、cache無効化の再実行。 | S中。全membership handler未確認。 |
| I05 | Q03/Q10: managerの複数保存・新blob孤立 | 改善推奨／中中中中 | profile変更の保存回数を整理、upload孤立回収と削除taskを耐久化。If-Match有無の契約を明示。 | S高。phone変更時の先行保存に注意。 |
| I06 | Q03/Q04/Q06/Q11: token/session別更新 | 優先見直し／高中中中 | old-token CAS＋session更新を一境界にするか、rotation operationと結果照会を設計。並行旧tokenで一結果へ収束。 | S高。session欠如互換の維持判断が必要。 |
| I07 | Q01/Q03/Q05: manager失敗でもtrue | 優先見直し／高中高中 | IdentityResultを必ず検査。disable/key resetの一体性または補修状態を設計。manager保存失敗fixtureで成功を返さない。 | S高。Enroll全体は未確認。 |
| I08 | 同期stateは保存結果確認、auditは別保証 | 概ね妥当／高中中中 | この代表経路の同期stateに限定。反対方向更新のLWW、監査欠損要件を確認。共有基盤の監査保証を流用して過大認定しない。 | S高。端から端の監査・実環境は未確認。 |
| I09 | Q09/Q10/Q11: terminal早期returnでcache修復しない | 改善推奨／高中高中 | terminal再送でもcache同期を再実行、または失敗を耐久作業へ。DB保存後cache停止→再送で終端判定へ収束。 | S高。反映SLO次第で優先見直し。 |
| B01 | Q01/Q03/Q04/Q06: 台帳と残高乖離、承認後event欠落 | 優先見直し／高高高高 | wallet単位lock/CAS・再計算、同一operation重複排除、承認とOutbox原子化。異なる2入金の合計と台帳差額を照合。 | S高、競合未再現。精度・訂正方式未決。 |
| B02 | Q01/Q07/Q11: 前月snapshotへ現在月値 | 優先見直し／高高高高 | periodを測定APIへ渡し、期間確定値を耐久保存。月境界後でも前月値を復元。最新plan/gaugeを過去値と扱う契約も確認。 | S高。Redis周期counter＋既存snapshotなしが条件。 |
| B03 | Q03/Q06: 契約置換とinvoice、Inboxが分離 | 改善推奨／高中中中 | source event IDで業務重複排除、必要ならsubscription/invoice同境界、競合unique敗者再読込。 | S中〜高。event順序・契約変更仕様未決。 |
| B04 | Q04: 状態競合・発行成功の意味 | 仕様次第／高中中中 | version／条件付き遷移が必要か決める。必須通知なら明示起動記録追加。支払/取消競合の受入仕様を先に定義。 | S中。通常発行serviceに明示Outboxなし。 |
| T01 | Q03/Q04/Q09: Pending残留・二重開始 | 優先見直し／高中高高 | 耐久start request、未enqueue sweeper、active operation一意/CAS。同じrecord再開と新規再実行を分ける。保存→enqueue停止後も状態照会と復旧可能。 | S高。stepの実効果・retry実効値未確認。 |
| T02 | Q01/Q04/Q06/Q09: 積み増しlost update・再送二重延長 | 優先見直し／高高高中 | renewal operation ID、tenant単位直列化/CAS・再計算、event起動の原子化。2更新と同一更新再送を別々に試験。 | S高、競合未再現。operator補正は別契約。 |
| T03 | Q03/Q08/Q09: marker後Outbox失敗で永久skip | 優先見直し／高中高中 | noticeとOutboxを原子化、またはPending/Published状態で再開。Outbox失敗後の次scanで必ず再起動。 | S高。メール到達自体はN02と分離。 |
| T04 | Q10: DB失敗で旧object消失 | 優先見直し／高中中高 | pointer保存後に旧object削除、durable cleanup、孤立新object回収。DB失敗で旧URLが生存。 | S高。外部削除成功という条件付き。 |
| C01 | Q01/Q04/Q06: deltaの欠落・二重適用 | 優先見直し／高高高中 | 原子的加算＋負値条件、またはlock/CAS再計算。論理調整IDを持つ。delta総和と最終Stockを別接続で検証。 | S高、競合未再現。会計連携要件未決。 |
| C02 | Q04/Q05/Q07/Q11: thumbnail競合・逆順URL | 改善推奨／中中中中 | Product単位version/lock。FileAsset ID＋version等で最新参照へ収束し、reconcileを持つ。逆順2移動で最終URL一致。 | S高、順序反例は分析。画像の更新許容未決。 |
| C03 | Q04/Q05: 祖先検査と別行変更の競合 | 改善推奨／中中中中 | 階層単位直列化や適切な分離レベル。A→B/B→Aを同期点付き試験で一方拒否。孤立親・復元FKは追加調査。 | S高、cycle未再現。 |
| K01 | Q04/Q05/Q06: 状態・編集競合の契約不足 | 仕様次第／中中中中 | 必要ならticket version、期待状態付きcommand、comment operation ID。低影響編集に一律Outboxを追加しない。 | S中。削除復元handlerは未着手。 |
| H01 | Q01/Q04/Q06/Q09/Q11: repeat delete再減算、通知起動欠落 | 優先見直し／中高高中 | 初回削除にのみ親count変更、reply counter原子更新／再計算。mentionを必須なら耐久起動。削除2回でもcount同一。 | S高。件数と必須通知のHを分離。 |
| H02 | Q04/Q06: unique敗者の結果非収束 | 改善推奨／中中低低 | 二人DMの競合insertを既存IDへ収束。member/reaction duplicateを適切な成功結果へ。group DMを同じ方式でdedupしない。 | S高。集合編集handler全体は未確認。 |
| H03 | Q07: 既読watermark後退 | 改善推奨／中高中低 | 現在値との比較付き更新、mark-unreadは別操作。新→旧順の要求で最大既読値を維持。 | S高。H: 単調既読を採用する場合。 |
| F01 | Q03/Q04/Q06/Q09/Q11: finalize・quota・event不一致 | 優先見直し／高高高高 | Pending→finalized CAS、debit操作ID、DBからのquota照合、Outbox原子化。DB失敗・同時finalizeで1 debit。 | S高。quota有効構成、quarantine精算は要確認。 |
| F02 | transactionは改善済み、Q07/Q11と外部補償が残る | 改善推奨／高中中中 | atomic pointer/eventを維持。copy孤立回収、削除結果不明の区別、consumer最新状態照合。専用DB経路も実測。 | S高。非PGsingle-writer、G13に注意。 |
| F03 | Q03/Q04/Q06/Q09/Q10/Q13: 回復key消失・返金重複・状態変更競合 | 優先見直し／高高高高 | tombstone/cleanup taskを保持、削除確認、状態再検査を含むclaim/CASと一回返金、失敗集約または個別retry。外部停止中にkeyを失わない。 | S高。storage実装の失敗意味も確認必要。 |
| F04 | 失敗を残してjob retryへ伝播 | 概ね妥当／中中中中 | 読んだmigrationの再走査に限定。失敗fileと成功file混在試験、retry枯渇監視、孤立copy回収を追加。 | S高。登録済みjob・PG前提、Vなし。 |
| W01 | Q03/Q06/Q09/Q10: 起動欠落・受信成功後重複 | 優先見直し／高中高高 | event/subscriptionのdurable配送予定、stable logical delivery ID、receiver dedup、結果不明照会、Failed redrive。 | S高。必須配送か、受信契約・順序未決。 |
| N01 | Q06: notification重複生成 | 改善推奨／中高中低 | source event/recipientのunique、consumer効果と重複記録を同境界へ。SignalRはDB再取得で回復可と定義。 | S高。mark-allの条件付き更新は妥当。 |
| N02 | best-effortと必須到達の要件差 | 仕様次第／高中高中 | 必須ならemail delivery task、有限retry・終端失敗・provider照会。任意なら欠損許容を文書化し、Inbox完了を送信成功と呼ばない。 | S高。既存実装のbest-effortはD。 |
| A01 | Q01/Q06/Q09/Q12: 欠損・duplicate batch停止・commit成否不明 | 仕様次第／高高高高 | 欠損不可用途なら耐久搬送、ID重複許容sink、成功/試行区別、DLQ永続化・再投入。診断log用途とは分ける。 | S高。欠損不可採用なら優先見直し。 |
| A02 | 保持方針・tenant失敗の運用未決 | 仕様次第／高中中高 | 保持/hold/削除期限を合意、tenant別進捗・失敗集約、purge件数監視。境界時刻と再実行を実DB確認。 | S高。backupからの再出現も運用確認。 |

「高中高高」のような優先軸は、特定の経路の意味を説明するための相対評価である。高影響な未検証項目を低優先へ落とす意図はない。要件未決・未確認は別列のとおり解消する必要がある。

### 推奨する改善の着手順

| 段階 | 対象と理由 |
|---|---|
| 1. 必須保証・構成の確定 | 会計、権限、必須配送、監査、不可逆削除の成功条件を決める。専用DB＋consumerのG13を優先再現。間違ったDBでの検証や設計を避ける。 |
| 2. 正本の破壊・不可逆欠落防止 | B02の期間正確性、B01/C01/T02の加算競合、I03/I07の権限・設定成功、F03/T04の回復情報・object消失。 |
| 3. 起動と重複記録の境界 | I01のpublisher一本化→業務保存＋Outbox、T01の耐久起動、T03のmarker、F01のfinalize、W01のfan-out、G08の例外分類・consumer業務冪等性。 |
| 4. 派生値・順序・運用 | C02/F02のURL、H01/H03のcounter/watermark、cache修復、dead-letter/Failedの照会・redrive・alarm。 |
| 5. ルール化 | 実DBで受入条件を満たした方式をSkills例へ反映。調査の仮説をそのまま承認済み規約にしない。 |

これは業務影響ベースの全体順序であり、各修正の依存関係とは別である。Outbox修正だけで、wrong-period、lost update、外部補償、監査欠損を一括解決できない。

## 9. 停止・競合・再実行シナリオ

いずれもSから導いたシナリオで、障害注入によるVではない。

| 対象 | 停止点／競合 | 残る状態→再実行→結果 | 回復上の課題・検証条件 |
|---|---|---|---|
| I01 | user作成後、初期権限・Outbox前に停止 | userは存在→再登録はduplicate／既存user判定→後続欠落を補修する保証なし | 登録operationと完了stepを照会できること。各manager保存間で停止。 |
| B01 | 異なるtop-up二件が同じwallet残高を読む | 両方のledger insertは別ReferenceIdで成功可能→最終残高は後勝ち→台帳合計と乖離 | 別DbContext/接続、read後barrier。初期残高＋二入金とledger総和が一致。 |
| B02 | 月初、前月snapshotなしで前月invoice生成 | 前月指定→quotaは現在月keyを読む→前月snapshotとして保存→以後既存snapshotを再利用 | TimeProviderとRedis TTLを制御。前月末・月初・年越し、遅延job、再生成を試験。 |
| T01 | Pending保存後、enqueue前に停止 | jobなしPending→Start/Retryは進行中判定で拒否し得る→自動完了不能 | enqueue未完了を耐久recordから発見。request再送、server restart、job ID保存失敗を分ける。 |
| T03 | notice保存成功、Outbox追加失敗 | noticeのみ→jobはlogして継続→次scanはalreadyNotified→eventを永久skip | 同じvalidity/typeで次scanが起動を補修すること。notice/integration eventの両行照合。 |
| H01 | reply削除を二回要求 | 一回目でtombstone/count減算→二回目SoftDeleteはno-opだがparent再減算→count低下 | 親に他のreplyもあるfixtureで削除2回。domain単体のno-op試験だけでは不十分。 |
| F01 | quota debit後、FileAsset保存失敗 | counterだけ増える→Pendingへの再finalizeで再debit→DB/Redis乖離 | quota有効・Redis実装で試験。成功後Outbox失敗なら409再送でも欠落を補修すること。 |
| F02/C02/I05 | file A→Bのevent E1とB→CのE2が逆順 | consumer URLはA→E2はold B不一致でno-op、Inbox記録→E1でBへ移動→再送E2はInbox skip→Cに到達しない可能性 | helperは逐次重複を防ぐが順序収束ではない。逆順・並行・A/B往復を実bus＋Inboxで試験。最新FileAssetとの照合が必要。 |
| F03 | storage削除失敗、行削除成功 | objectだけ残る／quota返金も失敗し得る→次jobにcandidate keyなし→同じ走査から再開不能 | 削除task/keyを保持すること。storage失敗と応答不明、並行二jobの返金を分ける。 |
| W01 | 一subscriptionのenqueue失敗 | fan-outは他subscriptionを続行→handler正常→Inbox完了→失敗分は再fan-outされない | 配送予定をevent/subscription単位で保存し、失敗分だけ再起動。 |
| W01 | receiver成功後timeout／attempt保存失敗 | receiverは反映済み、senderは不明→HTTP/job retry→delivery IDが変わり重複効果 | 元eventまたはstable delivery operationでreceiver dedup。相手の記録と照合。 |
| N01/G08 | notification保存後、SignalR／Inbox前に停止 | notificationのみ→event再配送→新IDのnotification追加→重複 | consumerの一回効果とInboxを一境界にするかsource event unique。異常なInbox保存エラーを成功扱いしない。 |
| A01 | tenant Aのbatch保存後、tenant B保存失敗 | 全batch再試行→Aの同じPK insertで失敗→B未保存→DLQ送り | sinkが重複を受理し未保存分へ進めること。A保存済み+B失敗のfixture、DLQ再投入も同じIDで試験。 |
| G06 | batch処理中にclaim lease失効、旧worker継続 | 新workerが再claim→両workerの外部効果／processed更新が競合し得る | lease更新、所有権検査、consumer/receiver冪等性のどこで守るかを選び、clockとworkerを制御。 |
| G13 | 専用DBのOutboxをdrain後、consumer scope設定 | sourceは専用DB→IDのみtenant情報へ置換→tenant consumerはdefault接続へfall-throughし得る | 共有DBと専用DBに識別可能なfixture。handler/Inbox構築時の接続、tenant metadata、作用先を確認。 |

結果不明は「未実行」と区別する。例えばB01のcommit後応答喪失は、再送前にinvoice/request/ledgerを確認するべきであり、HTTPエラーだけを根拠に再creditしてはいけない。

## 10. 重点検証の実施結果と残る試験

### 10.1 初回の実行試行と依存更新後の追試

logger・session内の出力先オプションを省略した再現用コマンド:

```powershell
rtk proxy dotnet test src\Tests\Framework.Tests\Framework.Tests.csproj --no-restore --filter "FullyQualifiedName~IdempotencyEndpointFilterReplayTests|FullyQualifiedName~EfCoreOutboxStoreTests|FullyQualifiedName~EfCoreInboxStoreTests|FullyQualifiedName~OutboxDispatcherHostedServiceTests" --verbosity quiet

rtk proxy dotnet test src\Tests\Framework.Tests\Framework.Tests.csproj --filter "FullyQualifiedName~IdempotencyEndpointFilterReplayTests|FullyQualifiedName~EfCoreOutboxStoreTests|FullyQualifiedName~EfCoreInboxStoreTests|FullyQualifiedName~OutboxDispatcherHostedServiceTests" --verbosity quiet
```

| 検証 | 結果 | 解釈 |
|---|---|---|
| 初回: `--no-restore`による重点Framework.Tests | `NETSDK1004`: `project.assets.json`なし | この試行ではテスト未実行。必要な依存復元がない状態。 |
| 初回: restore込みの同じ重点テスト | `NU1902`・`NU1903`がエラー。対象依存にはSystem.Security.Cryptography.Pkcs/Xmlが含まれる | この試行では復元段階で停止し、テスト判定なし。後述の追試とは区別する。 |
| 追試: 利用者のNuGet対応後、restore込みで同じfilterを実行 | **成功46件、失敗0件、スキップ0件、exit code 0** | 2026-10-15 12:10:18〜12:10:23 JST。Framework.Tests.dll、net10.0。TRXのexecuted/passedはともに46。 |
| 初回: 実DB用Dockerの起動可否 | `Cannot open shim file for read. Could not read shim file.`。標準Docker Desktopの代替実体もなし | PostgreSQL・Redis・storageの隔離障害試験は未実施。今回の追試はDockerを必要としない重点テストのみで、Docker起動可否は再確認していない。 |
| ソース・結果文書の静的確認 | handler数とmodule内訳、参照先・行範囲、機能ID対応を確認 | 文書の追跡可能性の確認であって、runtime保証の試験ではない。 |

追試は、HEAD `a967537526f3b769c7d268da3ed2a6920af65ce9`に利用者の依存更新2ファイルを加えた状態で実行した。アシスタントによる依存manifest変更・監査warningの抑制は行っていない。元コミットの依存構成での初回失敗を取り消すのではなく、更新後構成の別の検証結果として記録する。

結果ファイルはsessionの`files\quality-test-results\data-quality-framework-retry.trx`に保存した。実際の追試コマンドには、上記のrestore込みコマンドに加えて、`--logger "trx;LogFileName=data-quality-framework-retry.trx"`とsession内の`--results-directory`を指定した。repositoryにはTRXを追加していない。

### 10.2 追試した既存テストと証明しないもの

| テスト | 追試結果 | 確認した対象 | 未確認のまま残るもの |
|---|---|---|---|
| [IdempotencyEndpointFilterReplayTests][test-replay] | **39件成功** | real filter＋memory cache、Redis模擬実装でresponse status/body/header replay、tenant/caller/route分離、同key競合、client切断、TTL設定、cache/lock障害時の挙動等 | process killによるcommit/cache間停止、実Redisの複数instance・lease失効、本文同一性、response TTL満了後の業務再送。模擬実装の成功を実Redisでの保証へ拡張しない。 |
| [EfCoreOutboxStoreTests][test-outbox] | **1件成功** | SQLiteでevent ID/tenant/correlation/payloadの保存 | 業務contextと共有transaction、PostgreSQL claim、rollback、専用DB、所有権fencing。 |
| [EfCoreInboxStoreTests][test-inbox] | **3件成功** | SQLiteでmarker登録、handler別キー、逐次二重登録 | handler効果との原子性、並行consumer、非duplicate DbUpdateException。 |
| [OutboxDispatcherHostedServiceTests][test-dispatcher-hosted] | **3件成功** | mock storeと記録用scopeによりtargetごとのdrain、single target、一target障害時の他target継続とscope解放 | 実際のtenant DB接続、consumerのDB選択、event業務効果、process再起動後の回復。 |
| [WalletTests][test-wallet] | 今回のfilter外・未実行 | ソース読取では逐次credit/debit、同一通貨、非正値・過剰debit拒否を確認 | 別接続の異なるtop-up lost update、commit後応答喪失、残高とledger照合。 |

TTLのテストには、設定値をcacheへ渡すことの確認と、process内reservationの期限を0にして実行中でも別要求が再取得できることの確認がある。後者の成功は「leaseを超えても重複実行しない」ことを意味せず、G03/G04の保証外に対応する現在挙動を確認している。**46件成功は、調査で挙げた不整合経路が解消済みという意味ではない。** 第7・8・9節の機能別評価・障害分析は維持する。

### 10.3 必要な追加検証

| 試験群 | 最低限の受入条件 |
|---|---|
| 正本の値 | B02の前月値・年越し・遅延再実行・plan変更、金額丸めと保存精度、stockのoverflow境界。正しい期間と単位の値になる。 |
| 加算競合 | B01/C01/T02を別DbContext/接続、read後同期点で同時実行。異なる操作は総和を保ち、同操作再送は一回反映。 |
| 保存境界 | I01/I02/I03/I06/I07、B01/T03/F01で各保存間の失敗とcommit後応答喪失。部分成功を残さないか、状態照会・補修がある。 |
| 集合・階層 | C02の空画像集合への同時追加、C03の相互親、I03の同時管理者解除、H02の同時DM/reaction追加。制約と結果収束を両方確認。 |
| event・順序 | consumer保存後停止、Inbox失敗、F02逆順移動、G06 lease失効。重複・取りこぼし・古い確定を防ぐ。 |
| 外部・起動 | T01のenqueue隙間、T04のblob差替失敗、F03の削除失敗・並行返金・restore/finalizeとの競合、W01のreceiver成功後応答喪失・fan-out一部失敗。回復情報を失わない。 |
| 監査・保持 | A01の一tenant成功後他tenant失敗、DLQ失敗・再投入、process kill。A02のcutoff/hold/再実行。契約した欠損・保持範囲と一致。 |
| 構成 | 共有DB／専用DB、複数instance、Redis有無、quota有効無効、event provider差。G13の実作用先を確認。 |

少数のmock試験や同一DbContextの逐次試験を、これらの代替証拠にしない。依存監査対応後の重点テストは成功したが、残る実DB・外部依存の試験には利用可能な隔離環境の確保が必要である。これらはアプリケーション修正とは分けて承認・実施する。

## 11. 未決の要件・運用条件

| 確認事項 | 判断への影響 |
|---|---|
| 成功応答の意味 | user作成だけか、初期権限・通知起動までか。invoice stateだけか、必須通知起動までか。transactionと回復記録の境界が変わる。 |
| 欠損・重複の許容 | SignalRは再取得可能でも、メール・webhook・監査・会計記録を同じbest-effortにしてよいとは限らない。 |
| 編集・順序の仕様 | LWWを許す編集と、Stock/残高の加算、非循環・単一thumbnail、単調既読を分ける。 |
| 課金の正本・時点 | quota counterを請求証拠に使うか、period確定値、過去のplan/rate、gauge測定時点、通貨丸めと訂正invoiceの扱い。 |
| 重複防止の期間 | client最大再送期間、Inbox/operation記録の保持、削除・restore・redrive後の再処理を一貫させる。既定HTTP TTL 24時間だけでは決められない。 |
| 外部受信契約 | event/delivery IDによるdedup、同キー異入力の拒否、結果照会、処理順序、receiver側保持期間。 |
| retryと復旧責任 | 自動試行の予算、再投入者、同じoperationの再開か新operationか、補償と訂正の権限・記録。 |
| 運用保証 | 未完了滞留、dead-letter、Failed job、quota差分、孤立object、audit DLQを誰がどの期限で検出・処理するか。 |
| 稼働構成 | dedicated DB tenantの範囲、実際のprovider、instance数、cache/job storeの共有、backup/PITR・復元後dedup。 |
| 保持・削除 | retention規約、legal hold、object削除期限、backupに残るデータ、tombstoneとoperation履歴をいつ削除できるか。 |

## 12. 共通ルール・Skillsへの抽出案

### 12.1 判断基準

方式を先に決めず、必要保証を先に出力する。現行helperの存在だけを合格条件にしない。

| 条件 | 判断・必要工程 | 今回の適用例 |
|---|---|---|
| 単一DB保存で目的が完結する | 一保存＋入力/DB制約。overwrite許容と結果不明時の照会を決める。無条件にOutboxやleaseを追加しない。 | I08のstate、B04のstate。 |
| 加減算の合計を保つ | 操作ID、原子的更新またはlock/CAS再計算、重複防止と効果の同時確定。 | B01/C01/T02。 |
| 集合・階層の条件を保つ | 保護する範囲を個別rowより広く定義。unique/FKだけで守れない条件は集約version/直列化等。 | I03/C02/C03。 |
| 成功後に必須の非同期処理がある | 起動要求を業務効果と耐久確定。enqueue前後の未完了発見、状態照会、有限retry後の要対応。 | I01/T01/T03/W01。 |
| consumerが業務効果を生成する | handler保存とdedupの一境界、source operation unique等。Inboxの存在だけでexactly-onceとしない。 | B03/N01/G08。 |
| 外部システムをまたぐ | 外部成功後の結果不明を定義、stable ID、外部照会、補償／訂正、耐久的な進捗。 | F01/F02/W01/N02。 |
| 清掃・不可逆削除がある | 削除確認までkey/入力を保持、claimと一回精算、tombstone、retry枯渇後の手動照会。 | F03/A02。 |
| 派生値・期間値を使う | 正本・時点・versionを明示、逆順対策、再計算／照合、許容遅延。 | B02/C02/H01/H03/I09。 |
| 欠損不可の監査が必要 | 耐久受理、重複許容sink、試行とcommit成功の区別、DLQ永続化・再投入。 | A01。 |

### 12.2 Skillの入力・出力仕様案

**入力**は、機能の成功・受理・完了、正本と更新集合、不変条件、並行利用、client再送、期間・精度、外部効果、許容遅延・欠損、tenant/DB構成、運用責任とする。

**出力**は次の固定項目を持つ保証契約とする。

```text
FeatureId / 詳細対象経路:
必要保証の根拠: D（仕様）/ H（未承認仮説）
成功・受理・完了:
正本 / 不変条件 / 精度・期間:
操作ID / 入力同一性 / dedup範囲・保持期限:
保存境界 / 読取競合・再計算 / 対象DB:
外部効果 / 結果不明 / 補償・訂正:
retry主体・単位・対象失敗・予算 / 増幅:
耐久入力・進捗 / restart / lease・旧worker:
枯渇後の状態 / 検出・照会・再投入:
Q01〜Q15の判定と理由 / 保証外 / 未決事項:
方式候補 / 採用理由 / 共有基盤で不足する保証:
受入試験 / 証拠区分 / 実施不能な試験:
```

Skillは上書き・欠損・外部成功の意味を勝手に低保証へ決めない。未決の重要条件は確認を要求する。`src\BuildingBlocks`の不足を見つけても、承認なしで共有基盤を変更しない。

### 12.3 既存Skillsとの接続と過剰対策の防止

| 接続候補 | 追加する判断工程 |
|---|---|
| add-feature / add-full-slice | 実装前に簡易保証契約。API replay、業務dedup、結果照会の違いを示し、必要なものだけ選ぶ。 |
| add-entity / create-migration | unique/FK/check/version/operation記録の必要性、soft-delete・restore・retentionとの整合を契約から導く。 |
| add-integration-event | publisher一経路、業務更新と起動の保存境界、consumer dedup、event順序とtenant metadata、dead-letter受入条件。 |
| testing-guide | 契約した保証から別接続競合・停止点・外部結果不明の試験を選択。mock・SQLite・PostgreSQLの証明範囲を区別。 |
| 新規write-quality設計Skill / rules案 | 条件分岐と例外・証拠の扱いを共通化。単一保存CRUDには大きなworkflowを要求せず、加算・外部・清掃には必要工程を追加。 |

方式を概念適用しても、I08/B04では同期state保存のためだけにOutboxを要求しない一方、B01/C01では一保存だけで合格にしない。T01/W01ではretry回数ではなく耐久起動を要求し、F03では削除前の情報保持と精算を要求する。この差をSkillの判定として説明できることを採用条件とする。

本節は仕様案であり、Skillsやrulesへ適用済みではない。実装例の採用には、重要な受入試験と運用契約を確認する必要がある。

## 13. 計画の実施状況

| 計画段階 | 今回の成果・状態 |
|---|---|
| 1 基準点・環境 | SHA・基本設定・未確認の稼働条件・隔離環境の制約を記録。 |
| 2 共通保証 | G01〜G13。実装条件と保証外を分離。 |
| 3 棚卸し | 102 commandと追加候補を39 IDへ分類。33代表経路の詳細分析、6棚卸しのみ。初期化・read-only等を理由付き除外。 |
| 4 必要保証・経路 | D/H、簡易保証契約、保存・retry・復旧一覧、15観点状態、停止シナリオを記録。個別未着手経路も明示。 |
| 5 重点検証 | 初回は復元で停止。利用者の依存更新後に重点Framework.Testsを再実行し46件成功。Dockerを使う実DB・外部依存の障害／競合試験は未実施。確認範囲と追加受入試験を記録。 |
| 6 評価・改善 | 差分、条件付き評価、4軸の優先理由、改善・受入条件を整理。 |
| 7 rules/Skills抽出 | 条件別判断、入力・出力、性質の異なる機能への適用例。変更・採用はしていない。 |

計画に沿った調査結果の文書化は完了したが、全機能の詳細検証や実DBでの障害再現が完了したものではない。未確認・要件未決・実施不能の項目を残した状態で、次の要件判断・修正・検証に使う文書である。

## 参照

すべて対象コミットのソースリンク。行範囲は今回の根拠箇所であり、実際に稼働する環境設定の証拠ではない。

[base-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Host/FSH.Starter.Api/appsettings.json#L165-L180
[base-db]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Persistence/Context/BaseDbContext.cs#L20-L95
[db-provider]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Persistence/OptionsBuilderExtensions.cs#L24-L106
[event-options]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/EventingOptions.cs#L6-L57
[idempotency]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Web/Idempotency/IdempotencyEndpointFilter.cs#L75-L302
[idempotency-options]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Web/Idempotency/IdempotencyOptions.cs#L1-L31
[outbox-store]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs#L44-L64
[outbox-enlist]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs#L188-L218
[outbox-claim]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs#L75-L180
[outbox-dispatch]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Outbox/OutboxDispatcher.cs#L42-L105
[event-bus]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/InMemory/InMemoryEventBus.cs#L54-L137
[rabbit-bus]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/RabbitMq/RabbitMqEventBus.cs#L58-L177
[inbox-store]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs#L23-L64
[domain-events]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Persistence/Inteceptors/DomainEventsInterceptor.cs#L34-L74
[jobs-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Jobs/Extensions.cs#L15-L95
[http-retry]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Web/HttpResilience/Extensions.cs#L13-L37
[http-options]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Web/HttpResilience/HttpResilienceOptions.cs#L6-L38
[tenant-scope]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventTenantScope.cs#L8-L42
[drain-scope]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/FinbuckleEventingDrainScope.cs#L8-L54
[confirm-email]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Users/ConfirmEmail/ConfirmEmailEndpoint.cs#L11-L24
[reg]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs#L33-L69
[reg-tail]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs#L249-L339
[reg-event]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Events/UserRegisteredEventHandler.cs#L18-L41
[password]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserPasswordService.cs#L68-L117
[password-expiry]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/PasswordExpiryService.cs#L75-L84
[password-history]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/PasswordHistoryService.cs#L66-L109
[user-roles]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserRoleService.cs#L113-L164
[role-perms]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Roles/RoleService.cs#L164-L242
[group-update]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Groups/UpdateGroup/UpdateGroupCommandHandler.cs#L39-L121
[group-add]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Groups/AddUsersToGroup/AddUsersToGroupCommandHandler.cs#L26-L77
[profile]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserProfileService.cs#L84-L185
[refresh]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/Tokens/RefreshToken/RefreshTokenCommandHandler.cs#L94-L126
[token-store]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/IdentityService.cs#L113-L134
[sessions]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/SessionService.cs#L327-L371
[twofactor-on]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/TwoFactor/VerifyEnroll/VerifyEnrollTwoFactorCommandHandler.cs#L22-L53
[twofactor-off]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Features/v1/TwoFactor/Disable/DisableTwoFactorCommandHandler.cs#L22-L47
[status]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/UserStatusService.cs#L20-L150
[grant]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Services/ImpersonationGrantService.cs#L55-L104
[billing-pay]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L184-L277
[wallet]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Domain/Wallet.cs#L34-L62
[ledger-unique]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/WalletTransactionConfiguration.cs#L16-L34
[billing-gen]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L44-L157
[usage]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/UsageReporter.cs#L34-L68
[usage-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/UsageSnapshotConfiguration.cs#L9-L23
[invoice-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/InvoiceConfiguration.cs#L16-L46
[monthly]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/MonthlyInvoiceJob.cs#L5-L36
[quota-period]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/BuildingBlocks/Quota/RedisQuotaService.cs#L150-L218
[subscription-maint]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/IntegrationEventHandlers/TenantSubscriptionMaintenance.cs#L14-L51
[subscription-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Data/Configurations/SubscriptionConfiguration.cs#L10-L25
[invoice-issue]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L160-L165
[invoice-void]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Billing/Modules.Billing/Services/BillingService.cs#L280-L299
[provision]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningService.cs#L35-L174
[provision-job]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningJob.cs#L34-L90
[provision-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Data/Configurations/TenantProvisioningConfiguration.cs#L8-L20
[renew]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantService.cs#L200-L230
[renew-event]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Features/v1/RenewTenant/RenewTenantCommandHandler.cs#L18-L49
[expiry-scan]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantExpiryScanJob.cs#L49-L147
[theme]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantThemeService.cs#L114-L180
[stock-handler]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Features/v1/Products/AdjustProductStock/AdjustProductStockCommandHandler.cs#L17-L32
[stock-domain]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Domain/Product.cs#L139-L154
[product-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Data/Configurations/ProductConfiguration.cs#L17-L58
[images]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Domain/Product.cs#L156-L237
[image-consumer]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/IntegrationEventHandlers/FileStorageKeyChangedProductImageHandler.cs#L20-L49
[image-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Data/Configurations/ProductImageConfiguration.cs#L17-L34
[avatar-consumer]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Identity/Modules.Identity/Events/FileStorageKeyChangedAvatarHandler.cs#L20-L42
[url-event]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files.Contracts/Events/FileStorageKeyChangedIntegrationEvent.cs#L26-L61
[category]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Catalog/Modules.Catalog/Features/v1/Categories/UpdateCategory/UpdateCategoryCommandHandler.cs#L22-L64
[ticket]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Tickets/Modules.Tickets/Domain/Ticket.cs#L88-L190
[ticket-comment]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Tickets/Modules.Tickets/Features/v1/Tickets/AddTicketComment/AddTicketCommentCommandHandler.cs#L30-L39
[chat-send]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Messages/SendMessage/SendMessageCommandHandler.cs#L79-L100
[chat-delete]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Messages/DeleteMessage/DeleteMessageCommandHandler.cs#L27-L59
[message]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Domain/Message.cs#L6-L158
[dm]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Channels/FindOrCreateDm/FindOrCreateDmCommandHandler.cs#L33-L67
[channel-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Data/Configurations/ChatChannelConfiguration.cs#L20-L33
[member-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Data/Configurations/ChannelMemberConfiguration.cs#L10-L27
[reaction-config]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Data/Configurations/MessageReactionConfiguration.cs#L10-L28
[chat-read]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Features/v1/Channels/MarkChannelRead/MarkChannelReadCommandHandler.cs#L31-L56
[member]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Chat/Modules.Chat/Domain/ChannelMember.cs#L28-L30
[upload-request]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Features/v1/RequestUploadUrl/RequestUploadUrlCommandHandler.cs#L69-L104
[finalize]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Features/v1/FinalizeUpload/FinalizeUploadCommandHandler.cs#L36-L115
[relocate]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Services/FileStorageRelocator.cs#L65-L119
[relocate-delete]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Services/FileStorageRelocator.cs#L128-L170
[purge-deleted]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Jobs/PurgeDeletedFilesJob.cs#L29-L131
[purge-orphan]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Jobs/PurgeOrphanedFilesJob.cs#L26-L109
[legacy-job]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Files/Modules.Files/Jobs/MigrateLegacyPublicFileKeysJob.cs#L46-L174
[webhook-module]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Webhooks/Modules.Webhooks/WebhooksModule.cs#L35-L59
[fanout]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Webhooks/Modules.Webhooks/Services/WebhookFanoutHandler.cs#L47-L109
[dispatch]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Webhooks/Modules.Webhooks/Services/WebhookDispatchJob.cs#L43-L176
[mention]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/MentionedInChannelIntegrationEventHandler.cs#L34-L83
[notify-read]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/Features/v1/MarkAllNotificationsRead/MarkAllNotificationsReadCommandHandler.cs#L15-L31
[invoice-mail]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/InvoiceIssuedEmailHandler.cs#L18-L35
[mail-sender]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Notifications/Modules.Notifications/IntegrationEventHandlers/BillingEmailSender.cs#L8-L35
[audit-channel]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Core/ChannelAuditPublisher.cs#L42-L95
[audit-worker]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Core/AuditBackgroundWorker.cs#L147-L200
[audit-sink]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/SqlAuditSink.cs#L23-L71
[audit-interceptor]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/AuditingSaveChangesInterceptor.cs#L21-L77
[audit-dlq]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/FileAuditDlqSink.cs#L9-L99
[audit-retention]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Modules/Auditing/Modules.Auditing/Persistence/AuditRetentionJob.cs#L36-L129
[test-replay]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Tests/Framework.Tests/Web/IdempotencyEndpointFilterReplayTests.cs#L25-L1265
[test-outbox]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Tests/Framework.Tests/Eventing/EfCoreOutboxStoreTests.cs#L22-L51
[test-inbox]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Tests/Framework.Tests/Eventing/EfCoreInboxStoreTests.cs#L9-L50
[test-dispatcher-hosted]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Tests/Framework.Tests/Eventing/OutboxDispatcherHostedServiceTests.cs#L19-L58
[test-wallet]: https://github.com/HiroshiYoshino/dotnet-starter-kit/blob/a967537526f3b769c7d268da3ed2a6920af65ce9/src/Tests/Billing.Tests/Domain/WalletTests.cs#L1-L71
