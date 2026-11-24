# WinPDF — Quality of Life (QoL) & Polish Roadmap (Pre-Step 4)

Checklist de melhorias de usabilidade, ergonomia visual (Fluent 2 / Grid de 4px) e recursos essenciais de leitura e edição do WinPDF.

---

## 📦 Etapa QoL 1: Barra de Status Inferior Nativa (Estilo Paint / Fotos / Notepad)
- [x] **Remoção dos Controles Antigos:**
  - Remover o seletor de páginas da barra de título superior (`MainWindow.xaml`), mantendo apenas o nome do documento limpo e centralizado.
  - Remover a pílula flutuante de zoom do `DocumentView.xaml`.
- [x] **Barra de Status Inferior Nativa (32 DIPs em Mica):**
  - Criar uma Status Bar fixa na base da janela com `Height="32"` integrada ao backdrop Mica (visível quando um documento está aberto e fora do modo Tela Cheia/Slides).
  - **Alinhados à Esquerda:**
    - Seletor de página compacto estilo *"Inline Pill"* (`Página [ X ] de Y`): `<TextBox>` camaleão com `Height="24"`, `MinHeight="24"`, largura adaptativa (`MinWidth="34"`, `MaxWidth="56"`), auto-select ao focar/clicar (ou via `Ctrl+G`), confirmação com `Enter` e cancelamento com `Esc`.
    - Divisor vertical `|`.
    - Tag de formato `PDF` (preparado para futuros formatos).
    - Divisor vertical `|`.
    - Botão sutil unificado `[ ⓘ  [Ícone Tamanho]  X.X MB/KB ]` (os 3 elementos juntos formam um único botão clicável que abre as **Propriedades do Documento** `Alt+Enter`).
  - **Alinhados à Direita:**
    - `[ToggleButton: Exibição Dupla (Duas Páginas on/off)]`
    - Divisor vertical `|`
    - `[ToggleButton: Rolagem Contínua (Contínuo on/off)]`
    - Divisor vertical `|`
    - `[Botão Fit Vertical (Ajustar à Página)]` `.` `[Botão Fit Horizontal (Ajustar à Largura)]` `.` `[ComboBox/DropDown de Zoom (100% ▾)]` `.` `[Botão - Zoom]` `.` `[Slider Horizontal Compacto de Zoom]` `.` `[Botão + Zoom]`
    - Divisor vertical `|`
    - `[Botão Apresentação de Slides (F5)]` `.` `[Botão Tela Cheia (F11)]`

---

## 📦 Etapa QoL 2: Matriz de Modos de Exibição, Preservação de Estado e Splitter Limpo
- [x] **Matriz Funcional de Modos de Exibição (2×2 via Toggles da Barra Inferior):**
  - **Contínuo ON + Duplo OFF:** Rolagem vertical contínua de 1 coluna.
  - **Contínuo OFF + Duplo OFF:** Modo Slide / Página Única (com *swipe* horizontal em telas de toque e botões discretos `<` e `>` nas extremidades esquerda/direita ao passar o mouse).
  - **Contínuo ON + Duplo ON:** Rolagem vertical contínua exibindo pares de páginas lado a lado.
  - **Contínuo OFF + Duplo ON:** Modo Livro paginado exibindo 2 páginas lado a lado por vez.
- [x] **Preservar Posição e Estado do Documento ao Abrir/Fechar Configurações:**
  - Manter `CurrentPageIndex`, `VerticalOffset`, `HorizontalOffset` e `ZoomFactor` intactos ao alternar para a `SettingsView` e voltar.
- [x] **Divisor da Barra Lateral (Splitter) Invisível:**
  - Remover a pílula visual aparente do `SplitterContainer`, mantendo apenas a área transparente de redimensionamento (`SizeWestEast`).

---

## 📦 Pacote QoL 3: Barra Lateral 2.0 (2 Colunas, Proporção Real e Sincronização)
- [x] **Layout de 2 Colunas para Miniaturas:** Expandir a barra lateral esquerda para exibir 2 tiles por linha.
- [x] **Proporção Real da Página nas Miniaturas:** Respeitar o `AspectRatio` real (`WidthPt / HeightPt`) de cada folha sem forçar A4.
- [x] **Sincronização Automática da Barra Lateral:** Acompanhar a página atual com `ScrollIntoView` suave durante a rolagem do documento.
- [x] **Modo Noturno de Leitura na GPU (Inversão Inteligente via Win2D).**
- [x] **Modo Noturno nas Miniaturas da Barra Lateral:** Propagar `IsNightMode` para `Thumbnails` no `DocumentViewModel` e aplicar inversão instantânea RGB bit a bit (`pixel ^ 0x00FFFFFF`) no `ThumbnailPresenter` (preservando o cache sem re-renderização no PDFium).

---

## 📦 Pacote QoL 4: Teclado, Botão Direito, Modo Noturno e Pesquisa
- [x] **Correção de Foco e Controle por Teclas Direcionais (`Up`, `Down`, `Left`, `Right`).**
- [x] **Atalhos Clássicos:** `PageDown`/`PageUp`, `Espaço`/`Shift+Espaço`, `Home`/`End`, `Ctrl+Roda do Mouse`, `Ctrl+0`, `F11`, `F5`, `Ctrl+R` / `Ctrl+Shift+R`.
- [x] **Menu de Contexto (Botão Direito do Mouse na Página).**
- [x] **Botão de Pesquisa no Documento Funcional (Ctrl + F):** Botão de pesquisa abre uma barra no topo da interface, logo abaixo da titlebar e centralizada, com uma barra de busca, botões anterior e próximo e botão para diferenciar maiúscula de minúscula.

---

## 📦 Pacote QoL 5: Persistência em Disco, Reabrir Último Documento, Metadados e Arquivos
- [x] **Persistência de Configurações em Disco (`%LOCALAPPDATA%\WinPDF\settings.json`).**
- [x] **Opção "Reabrir último documento ao iniciar" nas Configurações.**
- [x] **Lembrar Última Página por Documento:** Retoma a leitura na última página visualizada de cada documento ao reabri-lo.
- [x] **Manter Nível de Zoom por Documento:** Preserva e restaura o nível de zoom individual de cada documento ao reabri-lo.
- [x] **Seção "Avançado" nas Configurações:** Seletor de idioma de exibição do aplicativo com "Padrão do Sistema", separador e opções em ordem alfabética ("English", "Português (Brasil)").
- [x] **Propriedades do Documento Reais (`Alt+Enter` e Clique na Barra Inferior via `FPDF_GetMetaText` em Diálogo Nativo Modal estilo Reprodutor Multimídia com obscurecimento de fundo e coluna única).**
- [x] **Ações de Arquivo (`Ctrl+S` Salvar Como via `FPDF_SaveAsCopy` e `Ctrl+Shift+N` Abrir uma Cópia).**
- [x] **Impressão**: abrir a janela de impressão traz a janela nativa do Windows (`PrintManagerInterop` + `PrintDocument` com Preview ao vivo e fallback Win32).

---

## 📦 Seleção e Pesquisa de Texto (PDFium Text APIs + Highlight na GPU)
- [x] **Interop Nativo de Texto e Busca (`PdfiumNative.cs`):**
  - Mapear as APIs de página de texto do PDFium: `FPDFText_LoadPage`, `FPDFText_ClosePage`, `FPDFText_CountChars`, `FPDFText_GetCharIndexAtPos`, `FPDFText_GetText`, `FPDFText_CountRects`, `FPDFText_GetRect` e `FPDFText_GetBoundedText`.
  - Mapear as APIs de busca: `FPDFText_FindStart`, `FPDFText_FindNext`, `FPDFText_FindPrev`, `FPDFText_GetSchResultIndex`, `FPDFText_GetSchCount` e `FPDFText_FindClose`.
- [x] **Motor de Busca Real no Documento (`Ctrl + F` / `F3` / `Shift + F3`):**
  - Executar a varredura de texto de todas as páginas em background na thread dedicada do `PdfiumEngine` sem bloquear a UI Thread.
  - Extrair os retângulos delimitadores (`Rect` em coordenadas PDF convertidos para DIPs via `PageCoordinateTransform`) de cada ocorrência encontrada.
  - Renderizar o destaque (*highlight*) das ocorrências no `PdfPageCanvas` via Win2D (amarelo translúcido para ocorrências na página e laranja/azul de destaque para a ocorrência ativa).
  - Exibir o contador real (`X de Y`) na barra flutuante de pesquisa (`FloatingSearchBar`) e rolar automaticamente a página/viewport até centralizar a ocorrência ativa ao clicar em Anterior/Próximo (`Enter` / `Shift+Enter`).
- [x] **Seleção Interativa de Texto com o Mouse e Cópia (`Ctrl + C`):**
  - Detectar quando o cursor passa sobre caracteres de texto (`FPDFText_GetCharIndexAtPos`) e alternar o cursor do mouse para `IBeam` (quando `ViewModel.ActiveTool == AnnotationTool.Select`).
  - Permitir clicar e arrastar na página (e duplo clique para selecionar palavra inteira) calculando os retângulos selecionados via `FPDFText_CountRects` e `FPDFText_GetRect`.
  - Desenhar a camada de seleção azul translúcida com cantos suaves sobre o texto no `PdfPageCanvas`.
  - Habilitar `Ctrl + C` e a opção **"Copiar"** no menu de contexto do botão direito, extraindo a string UTF-16 selecionada via `FPDFText_GetText` e enviando para o `Clipboard` do Windows.
- [x] **Links Clicáveis Internos e Externos (`FPDFLink_*`):**
  - Mapear `FPDFLink_GetLinkAtPoint`, `FPDFLink_GetDest` e `FPDFLink_GetAction` para permitir clicar em links de sumário/rodapé dentro da página (navegando para a página destino) ou abrir URLs externas no navegador com confirmação.

---

## 📦 Experiência de Toque e Gestos (`v0.5.x` — Concluído)
- [x] **Deslize Lateral Interativo de Slides (Modo Não Contínuo e Apresentação `F5`):**
  - Gesto de arraste horizontal com 1 dedo na tela sensível ao toque e 2 dedos no Precision Touchpad (`AdjacentSlidePreviewContainer` com prefetch via `TileCache` Nível 0 e `s_slideBuffer`).
  - Resistência elástica de borda (*rubber-banding*) na primeira/última página e física de *commit* vs. *bounce-back* suave (`CubicEase.EaseOut`), mantendo teclado, roda do mouse e botões laterais instantâneos.
- [x] **Seleção de Texto por Toque (Segurar ~320 ms e Arrastar) + Barra Flutuante Nativa (`CommandBarFlyout`):**
  - Reconhecimento de *long-press* sobre texto sem interferir na rolagem rápida por toque, alças de ajuste de início/fim (`TouchSelectionStartHandle` / `TouchSelectionEndHandle`) e `TouchSelectionCommandBarFlyout` acima da seleção (expandido na `v0.6.0-alpha.6` com Copiar, Marca-texto, Comentário, Sublinhado, Tachado, Sublinhado Ondulado e 6 cores).
  - *Long-press* em área vazia da página abre criação de nota de comentário (`FPDF_ANNOT_TEXT`).
- [x] **Barra de Rolagem Vertical Assistida para Toque (*Touch Scroll Scrubber*):**
  - Pílula Acrylic nativa (`TouchScrollScrubberContainer` / `TouchScrollPill`) exibida exclusivamente ao interagir por toque no modo Contínuo, com *fast scrubbing* 1:1, bolha de página ao vivo (`TouchScrollPageBubble`) e *auto-hide* após `2.2 s`.

---

## 📦 Anotações de Texto, Comentários, Formas Externas, Undo e Assinaturas Digitais (`v0.6.0-alpha.6` — Concluído)
- [x] **Interop Nativo de Anotações, Form XObjects e FormFill do PDFium (`PdfiumNative.cs`, `PdfiumEngine.cs`):**
  - Mapeamento completo de `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFPage_CloseAnnot`, `FPDFPage_CreateAnnot`, `FPDFPage_RemoveAnnot`, `FPDFAnnot_*`, `FPDFAnnot_SetAP` / `FPDFAnnot_GetAP`, `FPDFPath_*`, `FPDFFormObj_CountObjects` / `FPDFFormObj_GetObject`, `FPDFDOC_InitFormFillEnvironment` e `FPDF_FFLDraw`.
- [x] **Marcações de Texto (`Highlight`, `Underline`, `Squiggly`, `StrikeOut`) e 6 Cores Padrão:**
  - Marca-texto com `/AP` em modo `/BM /Multiply` (`ca 0.42`) e espaçamento vertical óptico (`-6%` na base e `+10%` no topo); Sublinhado (`-6%` abaixo da base), Sublinhado Ondulado (senoide contínua com ícone nativo `PathIcon` de `W` ondulado) e Tachado (`42%` da altura).
  - 6 cores predefinidas: Amarelo (`#FFFFE620`), Verde (`#FF4CD964`), Azul (`#FF4DA6FF`), Rosa (`#FFFF66B2`), Laranja (`#FFFF9F40`) e Vermelho (`#FFF24C4C`). Escolher uma cor nos menus apenas altera a cor ativa padrão sem criar marcação prematuramente.
- [x] **Notas de Comentário em Balão de Fala (`FPDF_ANNOT_TEXT`) e Menus Flutuantes Contextuais:**
  - Ícone vetorial `22×22 pt` de balão de fala criado via barra superior, botão direito em área vazia da página ou *long-press* de toque.
  - `MouseSelectionFlyout` compacto (ícones lado a lado + 6 cores) ao clicar com o botão direito sobre texto selecionado.
  - `AnnotationQuickBarFlyout` em Acrylic deslocado `20 DIPs` acima/abaixo da anotação (prevenindo exclusão acidental em duplo clique) e `AnnotationEditorFlyout` enxuto (botão único `×` no topo, Salvar + Excluir à esquerda na base e 6 cores à direita).
- [x] **Manipulação de Formas e Escritas de Outros Leitores (Mover, Copiar, Recortar e Colar):**
  - Arraste interativo (`AnnotationMoved` + `AnnotationDragGhostBorder`) e `Ctrl+C` / `Ctrl+X` / `Ctrl+V` para anotações não-textuais (`Ink`, `Square`, `Circle`, `Line`, `Polygon`, `PolyLine`, `FreeText`, `Stamp`), preservando cor e espessura originais de `Form XObjects` aninhados e colando exatamente na posição do último clique do mouse (`MouseClickedOnPage` + rastreamento de *light-dismiss*).
- [x] **Aba Lateral de Anotações Sem Flicker (`AnnotationsTabPage.xaml`) e Undo de 40 Níveis (`Ctrl + Z`):**
  - Atualização diferencial in-place de `AnnotationsSummary` (sem piscar ao mover ou trocar cor) com animações `AddDeleteThemeTransition` e `RepositionThemeTransition` ao criar/excluir.
  - Histórico `_annotationUndoStack` (até 40 ações) revertendo criação, edição de texto/cor, movimentação, recorte, colagem e exclusão com `Ctrl + Z`.
- [x] **Inspeção Universal de Assinaturas Digitais PKCS#7 e Carimbos Visuais (`PdfSignatureInspector.cs`):**
  - Renderização de carimbos visuais (`/FT /Sig`) no documento via `FPDF_FFLDraw`, indicador discreto na barra de status ao lado do tamanho do arquivo e exibição detalhada no diálogo unificado de **Propriedades do Documento** (`Alt+Enter` ou clique na barra de status / carimbo).

---

## 📦 Próximos Passos da Série `v0.6.x` (`v0.6.1+` — Caneta Livre, Borracha, Salvamento Direto e Ajustes)
- [ ] **Desenho Livre com Caneta / Tinta (`AnnotationTool.Pen`) e Borracha (`AnnotationTool.Eraser`):**
  - Capturar eventos de ponteiro (`PointerPressed`, `PointerMoved`, `PointerReleased`) no `PdfPageCanvas` respeitando `IsTouchDrawingEnabled` (separando pan/zoom com dedo de escrita com Stylus/mouse).
  - Desenhar o traço em tempo real na camada Win2D com latência zero durante o movimento e persistir `FPDF_ANNOT_INK` (`FPDFAnnot_AddInkStroke` + `/AP` stream) ao soltar o ponteiro.
  - Implementar a ferramenta **Borracha (`AnnotationTool.Eraser`)** para remover traços de tinta ou anotações tocadas/arrastadas.
- [ ] **Redo (`Ctrl + Y`), Salvamento Direto (`Ctrl + S`) e Aviso de Alterações Não Salvas:**
  - Complementar a pilha de Undo (`Ctrl + Z`) com **Redo (`Ctrl + Y` / `Ctrl + Shift + Z`)** e botões de Desfazer/Refazer na barra de ferramentas.
  - Implementar salvamento direto no arquivo atual (`Ctrl + S`), opção de salvamento automático de anotações e diálogo de confirmação ao fechar documento com `HasUnsavedChanges == true`.
- [ ] **Configurações de Anotação (`v0.6.1+`):**
  - Opção nas Configurações: *"Continuar editando anotações"* (manter ferramenta ativa após criar uma anotação em vez de voltar para a ferramenta de seleção/mão).
  - Opções nas Configurações para caneta/tinta: tempo de agrupamento entre traços (tinta multi-traço), suavização de tinta e comportamento da Stylus (caneta vs. mão).
- [x] **Menu Inserir na Barra Superior (`v0.6.1` — Comentário, Texto e Imagem):**
  - Novo botão `+` (`InsertMenuBtn` em `MainWindow.xaml` entre o lápis e o menu `...`) separando inserção de objetos das ferramentas de traço do lápis.
  - **Adicionar comentário** movido da barra flutuante do lápis para o menu Inserir (`StartInsertCommentCommand`).
  - **Adicionar texto (`StartInsertTextCommand`):** editor inline transparente com borda tracejada direto sobre a página do PDF e mini-barra Acrylic `12 DIPs` acima/abaixo (`Tamanho`, `Negrito`, `Itálico`, `Sublinhado`, `Cor`, `Excluir`, `Confirmar`), usando a família universal PDF Standard-14 `Arial / Helvetica` (Regular, Negrito, Itálico e Negrito+Itálico via `FPDFText_LoadStandardFont`), gravando texto e sublinhado 100% vetoriais em `FPDF_ANNOT_STAMP` (com metadados `/WinPDFStyle` e cálculo exato de limites sem corte), com suporte a mover, copiar/recortar/colar, reeditar pela QuickBar e Undo (`Ctrl+Z`).
  - **Inserir imagem** (`StartInsertImageCommand` + `FPDF_ANNOT_STAMP` via `FPDFPageObj_NewImageObj` / `FPDFImageObj_SetBitmap`) com suporte a transparência PNG, movimentação por arraste, redimensionamento interativo pelos 4 cantos (proporção travada + `Shift` livre), Copiar/Recortar/Colar, Undo (`Ctrl+Z`) e persistência no PDF.
  - Item **Inserir assinatura** ainda exibido no menu como *grayed out* (`IsEnabled="False"`) aguardando implementação.
- [ ] **Menu Inserir Fase 2 (`v0.6.1+` — Inserir Assinatura):**
  - **Inserir assinatura (rubrica visual):** diálogo modal para desenhar assinatura com mouse/caneta ou importar `.png` com fundo transparente, salvando assinaturas reutilizáveis localmente (`%LOCALAPPDATA%\WinPDF\signatures\`), carimbando como `FPDF_ANNOT_STAMP` e adicionando alças de redimensionamento nos cantos para assinaturas inseridas.
- [x] **Refinamentos Visuais e de Janela (`v0.6.1`):**
  - Borda de destaque da página atual nas miniaturas da barra lateral (`ThumbnailsTabPage.xaml`) perfeitamente retangular ao redor da página (`CornerRadius="0"`, `Margin="-1"`), sem cantos arredondados vazando.
  - Área do ícone e título `"WinPDF"` na barra de título integrada (`MainWindow.xaml.cs`) integrada à região nativa de arraste e duplo clique para maximizar/restaurar (`SetTitleBar(AppTitleBar)` + `InputNonClientPointerSource` passthrough apenas nos botões).
  - Caixa de destaque dos resultados de busca (`PdfPageCanvas.DrawTextOverlays`) ampliada proporcionalmente em torno do texto encontrado.
  - Suporte a reabertura manual da barra lateral em tela dividida (`SidebarManualMinWindowDip = 718.0`, `MinSidebarWidth = 218.0` em `MainWindow.xaml.cs`): a barra lateral continua fechando automaticamente abaixo de `950 DIPs`, mas permanece disponível para abertura manual até `718 DIPs` (mantendo no mínimo `500 DIPs` para a área do documento) e encolhe automaticamente os botões da barra inferior (`ShellViewModel.IsDocumentAreaNarrow`) quando a área útil do documento fica abaixo de `720 DIPs`.

---

## 📦 Planejado para `v0.7.x`: Sistema de Abas (Multi-Document Tabbed Shell) e Integração com Windows
- [ ] **Refatoração do Estado para Múltiplos Documentos (`ShellViewModel.cs` — `v0.7.0`):**
  - Criar `ObservableCollection<DocumentViewModel> OpenDocuments` e gerenciar a troca de `CurrentDocument` (aba ativa) preservando de forma independente o estado de cada documento (`CurrentPageIndex`, `ZoomFactor`, `ViewMode`, `LastVerticalOffset`, `LastHorizontalOffset`, histórico de busca, barra lateral e pilha de Undo/Redo).
  - Implementar suspensão/hibernação em disco de abas inativas em background para economizar RAM e liberar deterministicamente recursos nativos (`CloseDocumentAsync`, `TileCache` e bitmaps Win2D) ao fechar uma aba individual.
- [ ] **Barra de Abas Integrada ao Fluent Design (`MainWindow.xaml` — `v0.7.0`):**
  - Adicionar `TabView` nativo do WinUI 3 integrado ao Mica, exibindo nome do arquivo, indicador de modificação (`•` quando `HasUnsavedChanges == true`), botão de fechar (`×` / clique do meio) e botão `+` para abrir novo PDF.
- [ ] **Atalhos de Teclado, Sessão de Abas e Integração com o Shell do Windows (`v0.7.0`):**
  - `Ctrl + T` / `Ctrl + O`, `Ctrl + W` / `Ctrl + F4`, `Ctrl + Tab` / `Ctrl + Shift + Tab`, abertura/drop de múltiplos PDFs simultaneamente e restauração de sessão de abas em `AppSettings`.
  - Redirecionamento single-instance (`AppInstance.FindOrRegisterForKey`) para abrir arquivos `.pdf` do Explorer como novas abas na janela existente.
  - Menu **Compartilhar** nativo do Windows e lista de arquivos **Recentes** na Jump List do ícone na barra de tarefas (botão direito).
- [ ] **Formulários (AcroForms), Anexos, Marcadores, Logs, Traduções, Auto-Updater e Store (`v0.7.1+`):**
  - Exportação de anexos embutidos (`FPDFAttachment_GetFile` em `AttachmentsTabPage.xaml`).
  - Marcadores de usuário persistentes (`%LOCALAPPDATA%\WinPDF\bookmarks.json` em `BookmarksTabPage.xaml`).
  - Preenchimento interativo básico de formulários AcroForms.
  - Botão de acesso aos arquivos de log nas Configurações, traduções adicionais com seletor de idiomas em fase Beta, `UpdateService` via GitHub Releases e empacotamento `.msix` para a Microsoft Store.
  - Expansão contínua do `AutoTestRunner.cs` (`tools/Run-AutoTest.ps1`) cobrindo múltiplas abas e novos fluxos com zero vazamento de handles nativos.
