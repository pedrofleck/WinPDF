# Plano de Implementação: Experiência de Toque e Gestos no WinPDF (`touch-todo.md`)

Este documento detalha o plano arquitetural e técnico em **3 etapas independentes e incrementais** para elevar a experiência de toque (Touchscreen) e touchpad de precisão (Precision Touchpad) no WinPDF, preservando 100% dos comportamentos existentes de teclado, mouse e renderização Win2D/PDFium.

---

## Visão Geral e Restrições Globais

1. **Zero regressão nos fluxos de Mouse e Teclado**:
   - Setas direcionais (`Left`, `Right`, `Up`, `Down`, `PageUp`, `PageDown`, `Space`), roda de mouse física (`MouseWheel`), `Shift + MouseWheel` e cliques nos botões laterais (`PrevPageZone` e `NextPageZone`) devem continuar trocando slides **instantaneamente e sem animação** no modo Não Contínuo e no modo Apresentação (`F5`).
   - O zoom por pinça (*pinch-to-zoom* via `DirectManipulation` do `ContinuousScrollViewer`), `Ctrl + MouseWheel` e o autoscroll com botão do meio do mouse devem continuar funcionando sem interferência.
2. **Isolamento de Estado e Performance (Zero Idle CPU / Zero Vazamento de VRAM)**:
   - Qualquer `CompositionTarget.Rendering`, `DispatcherTimer` ou `Storyboard` criado para gestos de toque deve rodar exclusivamente durante o gesto ativo e desengatar imediatamente ao terminar, garantindo `0.0%` de uso de CPU em repouso.
   - Reutilizar o `TileCache` (bitmaps base Nível 0) e o `s_slideBuffer` (`PdfPageCanvas`) já existentes para que o slide adjacente apareça instantaneamente ao iniciar o arraste lateral, sem bloquear a UI thread.

---

## Etapa 1: Deslize Lateral Interativo de Slides (Modo Não Contínuo e Apresentação `F5`)

### Objetivo
Permitir passar páginas/slides deslizando horizontalmente no modo **Não Contínuo** (`!doc.IsContinuous`) e no modo **Apresentação (`F5`)** (`ViewModel.IsPresentationMode`), tanto com **1 dedo na tela sensível ao toque** quanto com **2 dedos em um Precision Touchpad**, com experiência visual contínua estilo **Xodo / aplicativo de Fotos de smartphone**:
- Conforme o usuário arrasta para a esquerda ou direita, o slide atual acompanha o dedo e o slide adjacente (próximo ou anterior) já entra lateralmente na tela em tempo real.
- Se o movimento for curto ou um *flick* acidental/fraco, ocorre uma animação suave de *bounce-back* (retorno elástico) mantendo o slide atual.
- Teclado, roda de mouse física e botões laterais (`PrevPageZone`/`NextPageZone`) continuam trocando de página **instantaneamente e sem animação**.

### Arquitetura e Arquivos Envolvidos
- `src/WinPDF/Views/DocumentView.xaml`
- `src/WinPDF/Views/DocumentView.xaml.cs`
- `src/WinPDF/Controls/PdfPageCanvas.cs` (apenas leitura/reuso do `TileCache` e `s_slideBuffer`)

### Detalhamento Técnico da Implementação

1. **Camada Visual de Transição Lateral (`DocumentView.xaml`)**:
   - Atualmente, no modo não contínuo, `DocumentViewModel.DisplayedRows` contém apenas `1` item (`DisplayedRows[0]`), renderizado dentro do `ContinuousPagesRepeater`.
   - Adicionar um container leve de pré-visualização do slide adjacente (`AdjacentSlidePreviewContainer`, inicialmente `Visibility="Collapsed"` e `IsHitTestVisible="False"`) dentro do mesmo sistema de coordenadas do `ContinuousScrollViewer` (ou sobreposto de forma sincronizada ao `ContinuousPagesRepeater`), contendo um par de `PdfPageCanvas` (`AdjacentLeftPageCanvas` e `AdjacentRightPageCanvas` para suportar também o modo Duas Páginas `IsDoublePage`).
   - Durante o gesto de arraste lateral (`_isInteractiveSlideSwipeActive == true`):
     - O slide atual (`ContinuousPagesRepeater`) é deslocado horizontalmente por `dragOffsetX` DIPs.
     - Se `dragOffsetX < 0` (arrastando para a esquerda → avançar para `curPage + step`), o `AdjacentSlidePreviewContainer` é vinculado à página `curPage + step` e posicionado em `TranslateX = dragOffsetX + viewportWidth + SlideGapDip` (`SlideGapDip = 24.0`).
     - Se `dragOffsetX > 0` (arrastando para a direita → voltar para `curPage - step`), o `AdjacentSlidePreviewContainer` é vinculado à página `curPage - step` e posicionado em `TranslateX = dragOffsetX - viewportWidth - SlideGapDip`.
     - Como o `PdfPageCanvas` consulta imediatamente o `_tileCache` (Nível 0) e o `s_slideBuffer` (que já faz prefetch de `currentIdx ± 1`), o slide vizinho aparece instantaneamente na primeira fração de milissegundo do arraste.

2. **Condições de Ativação e Guardas de Zoom**:
   - O gesto de swipe de slides só é ativado quando:
     - `ViewModel.CurrentDocument is { Pages.Count: > 0 } doc`
     - `!doc.IsContinuous || ViewModel.IsPresentationMode`
     - O slide está ajustado à tela (`_isFittedVertically` ou `ContinuousScrollViewer.ScrollableWidth <= 4.0`) **ou** o `ContinuousScrollViewer` já está encostado no limite horizontal correspondente ao sentido do arraste (`HorizontalOffset <= 2.0` ao arrastar para a direita, ou `HorizontalOffset >= ScrollableWidth - 2.0` ao arrastar para a esquerda).
   - Se o usuário colocar um **2º dedo na tela** (iniciando *pinch-to-zoom*), o swipe lateral é imediatamente cancelado (restaurando `TranslateX = 0` e ocultando `AdjacentSlidePreviewContainer`) para que o `DirectManipulation` do `ScrollViewer` execute o zoom por pinça normalmente.

3. **Captura de Toque de 1 Dedo (Touchscreen)**:
   - Monitorar `PointerPressed`, `PointerMoved`, `PointerReleased`, `PointerCanceled` e `PointerCaptureLost` (com `handledEventsToo: true` em `ScrollViewerContentRoot` / `ContinuousScrollViewer`) para `PointerDeviceType.Touch`.
   - Aplicar trava direcional (*Direction Lock*): aguardar o dedo mover pelo menos `10 DIPs` com `Math.Abs(dx) > Math.Abs(dy) * 1.25` antes de iniciar `_isInteractiveSlideSwipeActive = true`.
   - No modo não contínuo ajustado verticalmente (`_isFittedVertically`), configurar `HorizontalScrollMode="Disabled"` no `ContinuousScrollViewer` durante o toque de 1 dedo para impedir que o `DirectManipulation` roube o gesto horizontal, mantendo `ZoomMode="Enabled"` para pinça de 2 dedos.

4. **Captura de Deslize de 2 Dedos no Precision Touchpad**:
   - No Windows 11, o deslize horizontal de 2 dedos no Precision Touchpad dispara uma sequência contínua de eventos `PointerWheelChanged` com `Properties.IsHorizontalMouseWheel == true` (sem a tecla física `Shift` pressionada) e pequenos incrementos de `MouseWheelDelta` a cada frame (ex.: `4`, `8`, `15`), ao contrário de uma roda de mouse tradicional que envia pulsos discretos múltiplos de `120` ou `Shift + Scroll`.
   - Em `OnScrollViewerPointerWheelChanged`:
     - Se `props.IsHorizontalMouseWheel == true` e a tecla física `Shift` **não** estiver pressionada (`!isShiftDown`), em vez de chamar `doc.PreviousPage()`/`doc.NextPage()` no primeiro evento (o que causava saltos múltiplos e secos), alimentar o controlador de swipe interativo acumulando `_swipeDeltaX` proporcionalmente ao `MouseWheelDelta` e reiniciar um `DispatcherTimer` de término de gesto do touchpad (`~100 ms` sem novos eventos indica que o usuário tirou os dois dedos do touchpad).
     - Roda de mouse vertical (`!props.IsHorizontalMouseWheel`) e `Shift + Roda do Mouse` (`isShiftDown == true`) continuam chamando `doc.PreviousPage()` / `doc.NextPage()` **instantaneamente e sem animação**.

5. **Física de Decisão: Commit vs. Bounce-Back (Prevenção de Cliques/Flicks Acidentais)**:
   - **Resistência de Borda (*Rubber-banding*)**:
     - Se o usuário estiver na primeira página (`curPage == 0`) e arrastar para a direita (`_swipeDeltaX > 0`), ou na última página e arrastar para a esquerda (`_swipeDeltaX < 0`), aplicar amortecimento elástico (`_swipeDeltaX *= 0.25`) sem exibir slide adjacente e sempre executar *bounce-back* ao soltar.
   - **Critério de Confirmação (*Commit*) ao soltar**:
     - Confirmar a troca de slide somente se houver página de destino válida **E**:
       1. A distância arrastada atingir pelo menos **22% da largura do viewport** (`Math.Abs(_swipeDeltaX) >= viewportWidth * 0.22`), **OU**
       2. A velocidade horizontal de saída for um *flick* intencional (`Math.Abs(velocityX) >= 550.0` DIPs/s no mesmo sentido do deslocamento **E** `Math.Abs(_swipeDeltaX) >= 32.0` DIPs).
     - Caso contrário (movimento curto, *flick* pequeno/acidental ou reversão de direção no final), executar **Bounce-Back**: animar `TranslateX` de volta a `0.0` (`~200 ms`, `CubicEase.EaseOut`) mantendo o mesmo slide.
   - **Conclusão Atômica da Transição**:
     - Ao confirmar a troca, animar o slide atual até `±(viewportWidth + SlideGapDip)` e o slide adjacente até `0.0` (`~180 ms`, `ExponentialEase` / `CubicEase.EaseOut`).
     - No callback `Completed` da animação, atualizar `doc.GoToPage(targetPageIndex)`, zerar o `TranslateX` do repeater e ocultar o `AdjacentSlidePreviewContainer` no mesmo frame da UI thread para que a troca final seja 100% invisível e sem piscar.

### Checklist de Verificação da Etapa 1
- [x] Deslizar com 1 dedo na tela no modo Página Única / Duas Páginas (`!IsContinuous`) e no modo Apresentação (`F5`) move o slide atual junto com o dedo e revela o próximo/anterior lateralmente.
- [x] Deslizar com 2 dedos horizontalmente no Precision Touchpad produz a mesma transição contínua lateral.
- [x] Um toque rápido ou *flick* curto (< 32 DIPs ou baixa velocidade) faz *bounce-back* suave e permanece no mesmo slide.
- [x] Na primeira e na última página, o arraste oferece resistência elástica (*rubber-band*) e volta ao lugar.
- [x] Setas do teclado (`Left`/`Right`/`Up`/`Down`/`PageUp`/`PageDown`), roda do mouse vertical, `Shift + Roda do Mouse` e clique/toque nas zonas laterais (`PrevPageZone`/`NextPageZone`) continuam passando os slides **instantaneamente, sem animação**.
- [x] Zoom por pinça (*pinch-to-zoom*) com 2 dedos continua funcionando normalmente.

---

## Etapa 2: Seleção de Texto por Toque (Segurar e Arrastar) e Balão Nativo de Cópia

### Objetivo
Permitir selecionar texto via toque ao **segurar e arrastar com o dedo** (*long-press + drag*) sobre o texto do PDF e, assim que o usuário soltar o dedo, exibir um **balãozinho nativo do Windows 11** posicionado acima da seleção contendo inicialmente apenas o botão de **Copiar** (apenas ícone, preparado arquiteturalmente para receber mais ações no futuro). Ao tocar fora da seleção, ela deve ser desfeita e o balão fechado.

### Arquitetura e Arquivos Envolvidos
- `src/WinPDF/Controls/PdfPageCanvas.cs`
- `src/WinPDF/Views/DocumentView.xaml`
- `src/WinPDF/Views/DocumentView.xaml.cs`
- `src/WinPDF/ViewModels/DocumentViewModel.cs`

### Detalhamento Técnico da Implementação

1. **Reconhecimento de Segurar e Arrastar (*Long-Press + Drag*) em `PdfPageCanvas.cs`**:
   - Atualmente, `OnCanvasPointerPressed` ignora contatos `PointerDeviceType.Touch` na linha 714 para permitir que um deslize imediato role a página via `DirectManipulation`.
   - Implementar uma máquina de estados de seleção por toque em `PdfPageCanvas`:
     - Ao receber `PointerPressed` com `PointerDeviceType.Touch` (quando `IsSelectInteractionAllowed() == true` e `!ViewModel.IsTouchDrawingEnabled`):
       - Registrar `_touchPressDipPoint = pt.Position`, `_touchPointerId = e.Pointer.PointerId` e iniciar um timer de *long-press* de **`320 ms`** (`_touchHoldTimer`).
       - Disparar em paralelo um hit-test assíncrono (`_pdfService.HitTestPagePointAsync` com `tolerancePt: 12.0`) para verificar se há caracteres selecionáveis próximos ao ponto tocado.
     - **Cancelamento para preservar o scroll normal**:
       - Se o dedo se mover mais de `10 DIPs` **antes** dos `320 ms`, ou se um segundo dedo tocar a tela (pinça), ou se `PointerReleased`/`PointerCanceled` ocorrer antes dos `320 ms`, cancelar `_touchHoldTimer` imediatamente. Assim, a rolagem rápida por toque continua 100% livre.
     - **Ativação da Seleção por Toque (aos `320 ms`)**:
       - Se o dedo permaneceu dentro do raio de `10 DIPs` durante os `320 ms` e há texto próximo (`hit.CharIndex >= 0`):
         1. Ativar `_isTouchSelecting = true`.
         2. Chamar `CancelDirectManipulations()` no `ScrollViewer` ancestral (e desabilitar temporariamente `VerticalScrollMode`/`HorizontalScrollMode` durante o arraste ativo) + `CapturePointer(e.Pointer)` no `PdfPageCanvas`, garantindo que arrastar o dedo agora estenda a seleção de texto em vez de rolar a página.
         3. Selecionar imediatamente a palavra inicial sob o dedo (`SelectWordAtPointAsync`) e definir `_selectionAnchorCharIndex` para dar feedback visual imediato (destaque azul).
     - **Arraste Contínuo (`OnCanvasPointerMoved`)**:
       - Enquanto `_isTouchSelecting == true`, chamar `RequestDragSelectionUpdate(posDip, force: false, _selectionSessionId)` em tempo real conforme o dedo se move pela página.
     - **Soltar o Dedo (`OnCanvasPointerReleased`)**:
       - Finalizar a seleção no ponto final, restaurar os modos de scroll do `ScrollViewer` e disparar um evento `TouchSelectionCompleted` (também disparado após *double-tap* ou *triple-tap* por toque que selecione palavra/frase) informando o retângulo delimitador da seleção em DIPs da página.

2. **Balãozinho Nativo do Windows (`CommandBarFlyout`) em `DocumentView.xaml` / `DocumentView.xaml.cs`**:
   - Utilizar o controle nativo **`CommandBarFlyout`** do WinUI 3 (`Placement="Top"`, `AlwaysExpanded="True"`), que é o mini-toolbar flutuante padrão do Windows 11 (com Acrylic nativo, cantos arredondados, sombra `ThemeShadow` e suporte nativo para adicionar novos `AppBarButton` em `PrimaryCommands` ou `SecondaryCommands` no futuro).
   - Configurar inicialmente com **um único `AppBarButton` compacto de ícone**:
     - Ícone: `<FontIcon Glyph="&#xE8C8;" FontSize="15" />` (Copy).
     - `ToolTipService.ToolTip`: string localizada de Copiar (`Context_Copy`).
     - `Width="40"`, `Height="36"` (compacto, sem rótulo de texto visível, apenas o ícone).
     - Ação (`Click` / `Command`): executa `ViewModel.CopySelectedText()`, fecha o `CommandBarFlyout` e limpa ou mantém a seleção.
   - **Posicionamento Preciso Acima da Seleção**:
     - Converter o topo central da primeira linha (ou do bounding box visível) de `doc.ActiveSelection.Rects` das coordenadas do `PdfPageCanvas` para `ScrollViewerContentRoot` via `TransformToVisual`.
     - Exibir o `CommandBarFlyout` com `FlyoutShowOptions`:
       - `Position = new Point(selectionCenterX, Math.Max(12.0, selectionTopY - 8.0))`
       - `Placement = FlyoutPlacementMode.Top`
       - `ShowMode = FlyoutShowMode.Transient` (para não roubar foco nem bloquear o proximo toque na página).

3. **Soltar a Seleção ao Tocar Fora**:
   - Se houver uma seleção ativa (`doc.ActiveSelection is { HasContent: true }`) e o usuário der um toque simples (`PointerPressed` / `Tapped` por toque sem arrastar e fora de links) em qualquer área da página ou fora do texto selecionado:
     - O `CommandBarFlyout` é fechado (`Hide()`) e `doc.ClearSelection()` é chamado imediatamente, removendo o destaque azul da página.
   - Se o usuário rolar ou aplicar zoom no documento enquanto o balão estiver aberto, ocultar o balão durante o movimento.

### Checklist de Verificação da Etapa 2
- [ ] Deslizar o dedo imediatamente na página continua rolando o documento normalmente sem acionar seleção acidental.
- [ ] Segurar o dedo por ~320 ms sobre texto seleciona a palavra inicial e permite arrastar o dedo para expandir a seleção por caracteres/linhas.
- [x] Ao soltar o dedo após selecionar (por arraste, *double-tap* ou *triple-tap* em toque), o `CommandBarFlyout` nativo do Windows 11 aparece centralizado logo acima do texto selecionado contendo apenas o ícone de Copiar.
- [x] Tocar no ícone de Copiar copia o texto selecionado para a área de transferência e fecha o balão.
- [x] Tocar em qualquer lugar fora da seleção fecha o balão e limpa a seleção da página imediatamente.

---

## Etapa 3: Barra de Rolagem Vertical Assistida para Toque ("Touch Scroll Scrubber")

### Objetivo
Ao interagir por toque na área do documento em modo Contínuo, exibir na lateral direita do documento (e **somente** lá — nunca na Sidebar ou nas Configurações) um elemento de rolagem rápida ergonômico e fácil de segurar com o dedo (*Touch Scroll Pill / Scrubber*), permitindo percorrer documentos longos rapidamente com feedback visual da página.

### Arquitetura e Arquivos Envolvidos
- `src/WinPDF/Views/DocumentView.xaml`
- `src/WinPDF/Views/DocumentView.xaml.cs`

### Detalhamento Técnico da Implementação

1. **Design Visual Fluent 2 (`DocumentView.xaml`)**:
   - Adicionar dentro de `ScrollViewerContentRoot` (alinhado à direita, `HorizontalAlignment="Right"`, `VerticalAlignment="Top"`, `Canvas.ZIndex="92"`) um conjunto composto por:
     - **Hit-Target Ergonômico de Toque (`TouchScrollThumbHitTarget`)**: `Grid` transparente de `48×64 DIPs` (atendendo às diretrizes de ergonomia de toque do Windows de mínimo 44×44 DIPs) para que o dedo capture a alça sem esforço.
     - **Pílula Visual Nativa (`TouchScrollPill`)**: `Border` de `26×48 DIPs` (`CornerRadius="13"`, `Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"`, `BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"`, `BorderThickness="1"`, `Translation="0,0,20"` com `<Border.Shadow><ThemeShadow /></Border.Shadow>`), contendo duas setinhas chevrons verticais discretas (`&#xE70E;` no topo e `&#xE70D;` na base, `FontSize="10"`).
     - **Badge Flutuante de Página Durante o Arraste (`TouchScrollPageBubble`)**: `Border` em formato de pílula (`CornerRadius="14"`, `Padding="12,6"`, `AcrylicBackgroundFillColorDefaultBrush` + `ThemeShadow`) posicionada à esquerda da alça (`Margin="0,0,12,0"`), que aparece **somente enquanto o dedo está arrastando a alça**, mostrando o número da página em tempo real (ex.: `14 / 120`).

2. **Ciclo de Vida e Visibilidade Exclusiva por Toque (`DocumentView.xaml.cs`)**:
   - A pílula **só pode aparecer** quando todas as condições abaixo forem verdadeiras:
     - O último input na área do documento foi via **Toque** (`PointerDeviceType.Touch`).
     - `ViewModel.CurrentDocument is { IsContinuous: true }` e `!ViewModel.IsPresentationMode`.
     - O documento possui altura rolável significativa (`ContinuousScrollViewer.ScrollableHeight > ContinuousScrollViewer.ViewportHeight * 0.5`).
   - **Auto-Hide Inteligente**:
     - Quando o usuário rola o documento com o dedo, a pílula aparece com fade-in suave (`150 ms`) e acompanha a posição vertical da rolagem.
     - Um `DispatcherTimer` (`_touchScrollHideTimer`, `2.2 s`) oculta a pílula com fade-out suave após o término da rolagem (exceto enquanto o usuário estiver segurando a própria alça).
     - Se o usuário mover o mouse ou usar a roda do mouse, a pílula de toque se oculta imediatamente para não poluir o uso desktop tradicional.

3. **Física de Arraste Direto (Fast Scrubbing 1:1)**:
   - Posição vertical `Y` da alça: mapeada linearmente entre `TrackTopMargin = 16.0` e `maxThumbY = ContinuousScrollViewer.ActualHeight - 16.0 - 64.0` conforme a razão `ContinuousScrollViewer.VerticalOffset / ContinuousScrollViewer.ScrollableHeight`.
   - Quando o usuário toca e segura `TouchScrollThumbHitTarget`:
     - Capturar o ponteiro (`CapturePointer`), cancelar qualquer inércia ativa do `ContinuousScrollViewer`, expandir sutilmente a pílula visual (`ScaleX = 1.08, ScaleY = 1.08`) e exibir o `TouchScrollPageBubble`.
     - A cada `PointerMoved`, calcular `normalized = Math.Clamp((fingerY - grabOffsetY - TrackTopMargin) / trackScrollableHeight, 0.0, 1.0)` e aplicar `ContinuousScrollViewer.ChangeView(null, normalized * ContinuousScrollViewer.ScrollableHeight, null, disableAnimation: true)` sincronizando `UpdatePageProgressFromScroll` para rolagem instantânea em 60/120 fps.

### Checklist de Verificação da Etapa 3
- [ ] A alça de rolagem por toque só aparece na área do documento (em modo Contínuo) quando o usuário utiliza a tela sensível ao toque.
- [ ] Ao usar mouse/trackpad sem toque, apenas a barra de rolagem padrão do Windows é exibida.
- [ ] Segurar e arrastar a pílula com o dedo percorre documentos longos de ponta a ponta com fluidez, exibindo a bolha com a página atual ao lado da alça.
- [ ] A pílula desaparece suavemente após ~2,2 segundos sem interação por toque ou ao entrar no modo Apresentação (`F5`) / Não Contínuo.
