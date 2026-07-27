# Google Stitch Brief — PTG Oil System Current UI

Recreate the **existing** PTG Oil System design language. Do not redesign it,
modernize it, or introduce a new visual style.

## Product and direction

- Enterprise oil-trading and operations ERP.
- Default language: Persian/Dari.
- Default direction: RTL.
- English mode: LTR.
- Visual tone: clean, restrained, data-dense, professional, light enterprise UI.
- No decorative hero, glassmorphism, heavy gradients, neon colors, or dark mode.

## Shell

- Page background: `#FCFCFC`.
- Desktop sidebar: 224px wide, dark `#2A2D45`.
- Collapsed sidebar: 88px.
- Header: 56px high, sticky, visually flat, no heavy border or shadow.
- Main content max width: 1200px.
- Main gutters: 40px desktop, 32px laptop, 24px tablet, 16px mobile.
- At 992–1199px use mini sidebar.
- At 991px and below use a right-side off-canvas sidebar in RTL and left-side in LTR.

## Sidebar

- Use the existing white PTG logo directly on the dark surface; no light logo plate.
- All normal labels and icons are bright white.
- Main nav text: 15px, medium; active text: bold.
- Subnav text: 14px, medium.
- Nav item radius: about 8px.
- Hover: `rgba(255,255,255,0.05)`.
- Active: `rgba(142,147,201,0.10)`.
- Dividers: `rgba(255,255,255,0.08)`.
- Logout is separate warning pink `#FF9A9A`.

## Header

- Start: hamburger/sidebar toggle.
- End: search icon, fiscal-year selector, Afghanistan/UK language flag, circular user avatar.
- Icon buttons: approximately 40×40px.
- Search dialog: max width 640px, 14px radius, 52px search field.
- Account drawer: 360px wide, circular 96px profile avatar.

## Color system

### Brand and actions

- Brand/navigation purple: `#55588B`.
- Purple hover/dark: `#404268`.
- Purple light: `#7779A2`.
- Primary CTA blue: `#1877F2`.
- Primary CTA hover: `#0B5ED7`.
- Primary CTA tint: `#E7F0FE`.

Keep purple for identity, tabs, links, selection, and focus. Keep blue for
Create/Add/Save actions. Do not merge these roles.

### Surfaces and text

- Page: `#FCFCFC`.
- Card/field: `#FFFFFF`.
- Neutral: `#F5F7FA`.
- Soft surface: `#F7F8FB`.
- Table header: `#F4F6FB`.
- Divider: `#E5E7EB`.
- Field border: `#CFD1DE`.
- Main text: `#424242`.
- Secondary text: `#666B75`.
- Placeholder: `#9AA0B5`.

### Status

- Success: text `#63914A`, background `#F1F6EE`.
- Danger: text `#B80000`, background `#FAE6E6`.
- Warning: text `#B87708`, background `#FEF5E7`.
- Info: text `#006395`, background `#E6F1F6`.
- Viewed: text `#4D4F7D`, background `#EEEEF3`.
- Draft/inactive: text `#3B3B3B`, background `#ECECEC`.

## Typography

- Persian/Dari: Vazirmatn.
- English: Poppins.
- Page title: 30px, weight 600, line-height 1.25.
- Section title: 19px, weight 600.
- Card title: 17px, weight 600.
- Body/table cell: 15px, regular.
- Label/button: 14px, weight 600.
- Table header/status/caption: 13px, weight 500–600.
- Helper/error: 12–13px.
- Standard KPI value: 26px, weight 600.
- All money, rate, weight, quantity, and IDs use LTR tabular numerals inside RTL.

## Spacing and shape

- Base gap: 12px.
- Page/card padding: 16px.
- Large gap: 24px.
- Form grid: 32px column gap, 24px row gap.
- Form section separation: about 48–52px.
- General panel/card radius: 8px.
- Inputs: 8px radius.
- Buttons: 12–13px radius.
- Status badges: fully pill-shaped, 999px radius.
- Shadows are very soft:
  `0 1px 3px rgba(16,24,40,.04), 0 10px 28px rgba(16,24,40,.06)`.
- Never use heavy shadows.

## Page patterns

### List page

Use this order:

1. Page title and primary action.
2. Optional KPI grid.
3. URL-backed filter rail.
4. Flat data table.
5. Pagination/bulk actions.

### Form page

- Normal form max width: 860px.
- Wide line-item form max width: 1080px.
- Two columns on desktop; one column on mobile.
- Input/select height: 42px.
- Input padding: 0 12px.
- Save button: blue, minimum height 36px, about 84px wide.
- Cancel: transparent text action.
- Put uncommon fields in an Advanced disclosure; do not remove backend fields.

### Detail page

Use shared page header, optional stat cards, a text-only tab rail, and flat
information/table panels. Keep tab-owned stat cards visible only with their tab.

## Components

### KPI card

- Maximum four cards per row; two cards per row below 600px.
- Desktop gap: 18–32px.
- White card, 18px radius, very soft shadow.
- Aspect ratio: approximately 2.6:1; max height 175px.
- Physical layout: text on the left 45%, illustration on the right 55%.
- Text may be Persian/RTL, but numeric value is LTR.
- Title: 11.5–14px, semibold.
- Value: 21–29px, bold; shrink long values.
- Unit/trend: 10–12px.
- Use blue/white 3D WebP illustrations with transparent backgrounds.
- Do not put illustrations in separate frames.

### Table

- Full width inside horizontal-scroll wrapper.
- No decorative outer card shadow.
- Header background: `#F4F6FB`.
- Header: 13px/500, approximately 11px × 14px padding.
- Cells: 15px/400, approximately 12px × 14px padding.
- Row divider: `#E5E7EB`.
- Hover: `#F5F7FA`.
- Selected: `#F2F4FC`.
- Numeric columns are LTR and right/endpoint aligned consistently.
- Row actions appear on hover, keyboard focus, or selection.

### Filter

- GET/query-string state.
- Main field height: 48px.
- Selected chip height: 36px.
- Chip background: `#F2F4FC`.
- Chip/popover radius: 6px.
- Popover width: 240–420px; max height about 320px.

### Button

- Primary: blue `#1877F2`, white text.
- Secondary: `#F1F2F4`, muted gray text.
- Danger: `#CC0000` or soft red variant.
- Font: 14px/600.
- Minimum height: 36px.
- Radius: 12–13px.
- Icon-only targets: about 40–44px with accessible labels.

### Status badge

- Minimum height: 28px.
- Horizontal padding: 12px.
- Font: 13px/600.
- Fully rounded pill.
- Use only the semantic palette above.

### Tabs

- Text-only horizontal rail.
- Normal text: `#424242`.
- Active text/indicator: `#55588B`.
- 2px active indicator.
- 14px type.
- Horizontal scrolling on small screens.
- Instant switching: no loader, delay, or decorative transition.

### Modal

- White surface, soft dialog shadow.
- Typical radius is visually 16px in normal app modals.
- Header min height: about 68px.
- Backdrop opacity: approximately 0.22–0.30.
- Large page modal: max 1180×820px.
- Mobile modal: viewport minus 16px.

## Avatars, icons, and illustrations

- User/person avatars are circular.
- Table person avatar: 28px.
- Header avatar: about 40px.
- If no photo exists, use a white person icon on dark purple `#3C3F72`.
- General icons: Bootstrap Icons or matching simple line SVGs, 16–20px.
- Dashboard quick links use simple Solar-style line icons.
- KPI illustrations: cohesive blue/white/gray 3D business, finance, oil,
  transport, contract, and professional-person scenes.

## RTL and responsive rules

- Use CSS logical alignment and spacing.
- Persian labels align to the RTL start edge.
- Never reverse digit order; numbers remain LTR.
- Forms collapse to one column on mobile.
- KPI cards remain two columns on phones.
- Data tables scroll horizontally unless the page explicitly uses the existing
  responsive-row-card pattern.
- Tabs scroll horizontally.
- Respect reduced-motion preferences.

## Do not introduce

- A new palette or font.
- A light sidebar.
- Dark mode.
- Heavy gradients outside charts/skeletons.
- Glass blur.
- Large decorative hero sections.
- Heavy card borders or shadows.
- New radius values without matching an existing component.
- Page-specific replacements for the shared filter, table, form, tabs, status,
  KPI card, or shell.
