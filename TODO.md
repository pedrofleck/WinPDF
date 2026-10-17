# WinPDF — Quality of Life (QoL) & Polish Roadmap (Pre-Step 4)

Checklist de melhorias de usabilidade, ergonomia visual (Fluent 2) e recursos essenciais de leitura antes da implementação da camada de escrita/anotações (Etapa 4).

---

## 📦 Etapa QoL 1: Barra de Status Inferior Nativa (Estilo Paint / Fotos / Notepad)
- [x] **Remoção dos Controles Antigos:**
  - Remover o seletor de páginas da barra de título superior (`MainWindow.xaml`), mantendo apenas o nome do documento limpo e centralizado.
  - Remover a pílula flutuante de zoom do `DocumentView.xaml`.
- [x] **Barra de Status Inferior Nativa (32 DIPs em Mica):**
  - Criar uma Status Bar fixa na base da janela com `Height="32"` integrada ao backdrop Mica (visível quando um documento está aberto e fora do modo Tela Cheia/Slides).
  - **Alinhados à Esquerda:**
    - Seletor de página compacto estilo *"Inline Pill"* (`Página [ X ] de Y`): `<TextBox>` camaleão com `Height="22"`, `MinHeight="22"`, largura adaptativa (`MinWidth="28"`, `MaxWidth="56"`), auto-select ao focar/clicar (ou via `Ctrl+G`), confirmação com `Enter` e cancelamento com `Esc`.
    - Divisor vertical `|` (`AppBarSeparator`).
    - Tag de formato `PDF` (preparado para futuros formatos).
    - Divisor vertical `|` (`AppBarSeparator`).
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
- [x] **Propriedades do Documento Reais (`Alt+Enter` e Clique na Barra Inferior via `FPDF_GetMetaText`).**
- [ ] **Ações de Arquivo (`Ctrl+S` Salvar Como e `Ctrl+Shift+N` Abrir uma Cópia).**
- [ ] **Impressão**: abrir a janela de impressão traz a janela nativa do Windows para impressão de arquivo

## Etapa de seleção e pesquisa de arquivos
Todo

## Etapa de implementação de abas
Todo
