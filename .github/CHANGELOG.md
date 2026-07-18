# v1.0.0 - ローカルラプラシアン for YMM4

YukkuriMovieMaker4向けのローカルラプラシアンエフェクトプラグインの初回リリースです。
局所ラプラシアンフィルタで、しきい値σ未満の輝度変動と、σ以上の輝度変動を、エッジを保ったまま独立の係数で増減します。
出力は入力の画素とパラメータだけから決定論的に決まり、同じ設定では常に同じ結果になります。
計算はComputeSharpの計算シェーダーがDirect3D 12で実行し、YMM4のDirect3D 11側とは共有テクスチャおよび共有フェンスで接続します。
8言語のリソース構成のUIを備えます。

---

## 新機能

### 1. フィルタの計算パイプライン

`LaplacianRemappingPipeline`は、輝度、フィルタ、描画の3段階の計算シェーダーを`ComputeContext`へ記録して実行します。ピラミッドのバッファーは素材の大きさに応じて確保し、サイズが変わらないフレームでは再利用します。処理の流れは次のとおりです。

1. `LuminanceShader`が、各画素のプリマルチプライドアルファを解除し、Rec.709の重みで輝度を求めてピラミッドの最下段へ書き込みます。
2. `DownsampleShader`が、5タップのカーネルで輝度のガウシアンピラミッドを最上段まで構築します。
3. `RemapShader`が、基準輝度ごとにリマップ関数を最下段の輝度へ適用し、`DownsampleShader`がリマップ画像のガウシアンピラミッドを構築します。
4. `AccumulateShader`が、各段のラプラシアン係数を求め、係数位置のガウシアンピラミッド値を挟む2つの基準輝度のハット重みで出力ピラミッドへ加算します。基準輝度の数だけ3と4を繰り返します。
5. `CopyLevelShader`と`CollapseShader`が、入力のガウシアン残差を起点に出力ピラミッドを崩壊させ、出力の輝度を求めます。
6. `RenderShader`が、出力の輝度と入力の輝度の比を色へ適用し、矩形の範囲だけを描画します。

| シェーダー | 役割 |
|---|---|
| `LuminanceShader` | 画素から輝度を求める |
| `HashBoundsShader` | 入力のハッシュと不透明画素の範囲を集計する |
| `DownsampleShader` | ガウシアンピラミッドを1段構築する |
| `FillLevelShader` | 出力ピラミッドを初期化する |
| `RemapShader` | 基準輝度のリマップ関数を適用する |
| `AccumulateShader` | ラプラシアン係数を重み付きで加算する |
| `CopyLevelShader` | 入力の残差を出力ピラミッドへ写す |
| `CollapseShader` | 出力ピラミッドを崩壊させる |
| `RenderShader` | 輝度の比を色へ適用して描画する |

### 2. 局所ラプラシアンフィルタと高速アルゴリズム

フィルタの計算は、Paris、Hasinoff、Kautzの論文「Local Laplacian Filters: Edge-aware Image Processing with a Laplacian Pyramid」（2011年）に基づきます。リマップ関数は同論文の式1をそのまま使用します。

- ディテール: 基準輝度との差がσ以下の成分は、振幅比のα乗で増減します。αはディテールのパラメータから0.25〜4へ割り当てます。
- トーン: 差がσを超える成分は、超過分をβ倍します。βはトーンのパラメータから0〜2.5へ割り当てます。
- ノイズ増幅の抑制: αが1未満のときは、同論文の5.2節に従い、最大輝度の1%から2%のスムーズステップで恒等写像と混合します。

原法は係数ごとにピラミッドを再構築するためO(N logN)かかります。本実装はAubryらの論文「Fast Local Laplacian Filters: Theory and Applications」（2014年）の離散化を用います。輝度域を等間隔に分割した基準輝度γについてだけリマップ画像のピラミッドを構築し、各係数を、係数位置のガウシアンピラミッド値gを挟む2つのγのハット重みで線形補間します。重みの和は1で、ディテール0かつトーン0のとき出力は入力と一致します。

ピラミッドはBurt–Adelsonの5タップカーネルで構築します。段数は素材の短辺が2になるまでの完全な深度を解像度から自動で決めます。中間の深度で打ち切るとハローが発生するためです。残差は原法どおり入力のガウシアン残差を使用します。乱数は使用せず、すべての値が入力から決定論的に決まります。

### 3. 可視範囲への出力矩形の最小化

入力のハッシュを集計するパスで、不透明画素のバウンディングボックスを`InterlockedMin`と`InterlockedMax`で同時に求めます。読み戻す量は8個の整数だけで、画素は読み戻しません。

- 矩形は4画素境界へそろえます。
- 出力テクスチャは矩形の大きさで確保し、Direct2Dの`Crop`と`AffineTransform2D`で元の位置へ合成します。
- 本エフェクトは素材の外側に画素を作らないため、計算領域は素材の範囲そのもので、余白を確保しません。

### 4. 構造キャッシュ

出力を決める入力が変わらないフレームでは、フィルタの計算を再利用します。

- 入力の画素は、各画素のパック値と位置のハッシュの総和とXORの2値へ集約し、8個の整数の読み戻しで前フレームと比較します。
- 画素のハッシュ、素材の大きさ、品質、ディテール、トーン、しきい値が一致する場合は、フィルタ段階を実行しません。
- 強さはDirect2D側の合成だけで反映するため、強さだけを変えたフレームでは再計算が発生しません。

### 5. Direct3D 11・Direct3D 12相互運用

`LaplacianRemappingGpuInterop`は、YMM4のDirect3D 11・Direct2D側と、ComputeSharpのDirect3D 12側を接続します。ComputeSharpの`GraphicsDevice`は、YMM4が使うDXGIアダプターのLUIDと一致するものを選びます。

入力と出力は、ComputeSharpで確保した共有テクスチャをDirect3D 11のテクスチャとして開き、Direct2Dのビットマップとして扱います。入力のテクスチャは素材の大きさで確保し、出力のテクスチャは可視範囲の矩形を収める容量で確保して拡大時だけ作り直します。両デバイスの同期は、Direct3D 12のフェンスを共有フェンスとしてDirect3D 11側で開いて行います。

`BeginCompute`は、Direct3D 11のコマンドを送出したうえでDirect3D 12側を待機させ、`EndCompute`は、Direct3D 12側の完了をDirect3D 11側で待ちます。

Direct3D 12デバイスの取得や共有リソースの作成に失敗した場合は、`TryCreate`が`null`を返し、エフェクトを適用せず入力映像を表示します。

### 6. カスタムシェーダーによる合成

`LaplacianRemappingCustomEffect`は、`[CustomEffect(2)]`の2入力エフェクトです。入力0は元映像、入力1はフィルタ処理した映像です。ピクセルシェーダー`LaplacianRemapping.hlsl`の`main`は、`amount`が0以下のとき元映像をそのまま返し、そうでないときは処理結果のRGBをアルファでクランプし、`source + (result - source) * amount`の線形補間で元映像と処理結果を混合します。処理結果は元映像と同じアルファを持つため、混合は置き換えとして働きます。

定数バッファーは`Amount`と3つの詰め物で16バイトです。`MapInputRectsToOutputRect`は2つの入力矩形の和集合を出力矩形とします。素材の外側には描画しないため、出力範囲は素材より広がりません。

シェーダーリソース: `pack://application:,,,/LaplacianRemapping;component/Shaders/LaplacianRemapping.cso`（ps_5_0、`ShaderResourceUri.Get`が生成）

### 7. エフェクト定義とパラメータ

`LaplacianRemappingEffect`は、YMM4の映像エフェクトとして宣言されます。

`[VideoEffect]`属性は以下のパラメーターで宣言されます。

- 表示名: `Texts.LaplacianRemapping`（ローカライズキー、日本語では「ローカルラプラシアン」）
- カテゴリー: `VideoEffectCategories.Filtering`
- 検索タグ: `TagClarity`・`TagTone`・`TagDetail`
- `IsAviUtlSupported = false`によりAviUtl向けEXO出力は非対応
- `ResourceType = typeof(Texts)`でローカライズリソースを指定

公開プロパティは以下のとおりです。基本項目は「基本」グループ、調整項目は「調整」グループに属します。

| プロパティ | 型 | デフォルト | 内部範囲 | アニメーション |
|---|---|---|---|---|
| `Amount` | `Animation` | 100 | 0〜100 | あり |
| `Quality` | `LaplacianRemappingQuality` | `High` | — | なし |
| `Detail` | `Animation` | 50 | -100〜100 | あり |
| `Tone` | `Animation` | 0 | -100〜100 | あり |
| `Threshold` | `Animation` | 30 | 1〜100 | あり |

`GetAnimatables`は`Amount`・`Detail`・`Tone`・`Threshold`を返します。

`CreateExoVideoFilters`は空のシーケンスを返します（EXO非対応）。`CreateVideoEffect`は映像処理用のインスタンスを生成します。エフェクトを最初に生成したときに、更新確認を一度だけ開始します。

### 8. フレームごとの更新

各フレームでYMM4の`EffectDescription`からフレーム位置、アイテム長、FPSを取得し、アニメーション値を評価します。値をパイプラインが前提とする範囲へ制限してから転送します。

| パラメータ | 変換 |
|---|---|
| `Amount` | `value / 100` をカスタムシェーダーの`Amount`へ |
| `Detail` | `value / 100` を-1〜1へクランプ |
| `Tone` | `value / 100` を-1〜1へクランプ |
| `Threshold` | `value / 100` を0.01〜1へクランプ |

強さが0以下のとき、またはディテールとトーンの両方が0のときは、フィルタを実行せず入力映像をそのまま出力します。入力の範囲が有限でない場合や、素材が大きさの上限を超える場合も、入力映像を表示します。

### 9. 品質設定

品質は、基準輝度のサンプル数を切り替えます。

| 品質 | 基準輝度のサンプル数 |
|---|---:|
| 標準 | 7 |
| 高品質 | 11 |
| 最高品質 | 15 |

ピラミッドの段数は品質によらず、素材の短辺が2になるまでの完全な深度を解像度から自動で決めます。計算領域は素材の範囲そのもので、長辺は8192画素、総画素数は16777216画素が上限です。

### 10. ローカライズ

`Texts`クラスは`[AutoGenLocalizer]`属性を持つ`partial`クラスとして宣言されます。
`YukkuriMovieMaker.Generator`のソースジェネレーターが`Texts.csv`を処理し、各ロケールのリソースファイルを自動生成します。

対応リソース: 日本語（`ja-jp`）・英語（`en-us`）・中国語簡体字（`zh-cn`）・中国語繁体字（`zh-tw`）・韓国語（`ko-kr`）・スペイン語（`es-es`）・アラビア語（`ar-sa`）・インドネシア語（`id-id`）

主なローカライズキーは以下のとおりです。

| キー | ja-jp |
|---|---|
| `LaplacianRemapping` | ローカルラプラシアン |
| `BasicGroup` | 基本 |
| `AdjustGroup` | 調整 |
| `Amount` | 強さ |
| `Quality` | 品質 |
| `Detail` | ディテール |
| `Tone` | トーン |
| `Threshold` | しきい値 |
| `QualityBalanced` | 標準 |
| `QualityHigh` | 高品質 |
| `QualityUltra` | 最高品質 |
| `TagClarity` | 明瞭度 |
| `TagTone` | トーン |
| `TagDetail` | 質感 |
| `UpdateAvailableMessage` | 新しいバージョン {0} が公開されています。 |
