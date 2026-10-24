from __future__ import annotations

import math
import re
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ATLAS_DIR = ROOT / "src" / "PTGOilSystem.Web" / "wwwroot" / "avatar"
TARGET_DIR = ROOT / "src" / "PTGOilSystem.Web" / "wwwroot" / "images" / "stat-cards"

ATLAS_FILES = {
    index: ATLAS_DIR / filename
    for index, filename in {
        1: "avatar_atlas_01_contracts_finance_transparent.png",
        2: "avatar_atlas_02_operations_inventory_transparent.png",
        3: "avatar_atlas_03_sales_costs_customs_transparent.png",
        4: "avatar_atlas_04_definitions_people_money_transparent.png",
        5: "avatar_atlas_05_hr_reports_detail_transparent.png",
    }.items()
}

MAPPING: dict[str, str] = {}


def assign(tile: str, group: str, *names: str) -> None:
    for name in names:
        MAPPING[f"{group}/{name}"] = tile


assign("A2-R2C1", "ref-blue", "ops-loading", "ops-loading-receipt")
assign("A1-R4C1", "ref-blue", "ops-settlement", "ops-stock-transfer")
assign("A2-R2C2", "ref-blue", "ops-transport")
assign("A4-R4C4", "ref-blue", "party-payment")
assign("A4-R4C3", "ref-blue", "party-receipt", "sale-loan")
assign("A4-R4C2", "ref-blue", "party-sarraf")
assign("A4-R3C5", "ref-blue", "party-supplier")
assign("A1-R1C5", "ref-blue", "sale-contracts")
assign("A5-R3C1", "ref-blue", "sale-deductions", "sale-expense")
assign("A3-R1C1", "ref-blue", "sale-revenue")

assign("A3-R1C4", "ref-contract", "contract-expenses")
assign("A1-R2C3", "ref-contract", "contract-loaded")
assign("A1-R3C3", "ref-contract", "contract-losses")
assign("A3-R1C1", "ref-contract", "contract-profit")
assign("A1-R3C4", "ref-contract", "contract-sales")

assign("A1-R1C5", "ref-contracts", "contracts-active")
assign("A1-R2C2", "ref-contracts", "contracts-purchase")
assign("A1-R2C3", "ref-contracts", "contracts-sale")
assign("A1-R2C1", "ref-contracts", "contracts-total")

assign("A4-R4C5", "ref-flow", "cards")
assign("A5-R3C3", "ref-flow", "cash-stack", "money-bag")
assign("A1-R4C3", "ref-flow", "coins", "drop")
assign("A5-R5C5", "ref-flow", "coins-calculator")
assign("A3-R1C4", "ref-flow", "doc-amount")
assign("A1-R2C1", "ref-flow", "doc-check", "receipt-check")
assign("A1-R2C3", "ref-flow", "doc-plus")
assign("A1-R4C4", "ref-flow", "docs-plus")
assign("A4-R1C4", "ref-flow", "fx-exchange")
assign("A3-R1C2", "ref-flow", "invoice-check")
assign("A1-R2C4", "ref-flow", "nozzle-drop")
assign("A4-R3C3", "ref-flow", "person")
assign("A4-R2C5", "ref-flow", "pin-destination")
assign("A4-R2C4", "ref-flow", "pin-origin")
assign("A3-R1C5", "ref-flow", "price-tag")
assign("A3-R4C5", "ref-flow", "stock-in")
assign("A1-R3C1", "ref-flow", "stock-out")
assign("A1-R1C1", "ref-flow", "tank")
assign("A1-R2C5", "ref-flow", "tank-gauge")
assign("A2-R1C4", "ref-flow", "truck")
assign("A3-R2C3", "ref-flow", "warning")
assign("A3-R3C3", "ref-flow", "weight", "weight-drop", "weight-minus", "weight-mt", "weight-person")

assign("A2-R4C1", "ref-icons", "analysis", "reports")
assign("A4-R4C5", "ref-icons", "bank-accounts")
assign("A1-R4C4", "ref-icons", "documents")
assign("A5-R1C1", "ref-icons", "employees")
assign("A4-R4C1", "ref-icons", "equipment")
assign("A5-R5C5", "ref-icons", "ledger")
assign("A4-R2C3", "ref-icons", "locations")
assign("A2-R1C3", "ref-icons", "pipeline", "refinery")
assign("A1-R2C4", "ref-icons", "production")
assign("A1-R4C3", "ref-icons", "products")
assign("A5-R3C5", "ref-icons", "profit-loss")
assign("A4-R3C1", "ref-icons", "pump-station")
assign("A3-R1C5", "ref-icons", "purchase-value")
assign("A3-R3C5", "ref-icons", "quality")
assign("A5-R4C4", "ref-icons", "rules-checklist")
assign("A3-R3C2", "ref-icons", "security")
assign("A4-R2C1", "ref-icons", "units")
assign("A2-R4C4", "ref-icons", "wagon")
assign("A4-R3C4", "ref-icons", "warehouse")
assign("A4-R1C4", "ref-icons", "r1c4")
assign("A1-R1C1", "ref-icons", "r1c6")
assign("A2-R1C4", "ref-icons", "r1c7")
assign("A1-R2C1", "ref-icons", "r2c1")
assign("A1-R1C5", "ref-icons", "r2c4")
assign("A4-R2C3", "ref-icons", "r2c6")
assign("A4-R2C4", "ref-icons", "r2c7")
assign("A4-R2C5", "ref-icons", "r2c8")
assign("A3-R1C1", "ref-icons", "r3c1")
assign("A3-R2C5", "ref-icons", "r3c3")
assign("A4-R4C5", "ref-icons", "r3c5")
assign("A3-R4C5", "ref-icons", "r3c6")
assign("A5-R4C4", "ref-icons", "r4c1")
assign("A5-R5C2", "ref-icons", "r4c2")
assign("A5-R1C2", "ref-icons", "r5c2")
assign("A5-R1C3", "ref-icons", "r5c3")
assign("A5-R1C4", "ref-icons", "r5c4")
assign("A5-R1C5", "ref-icons", "r5c5")
assign("A5-R2C1", "ref-icons", "r5c6")

assign("A5-R1C1", "ref-people", "customers", "emp-desk", "employees", "partners", "sup-total", "svc-total")
assign("A4-R3C5", "ref-people", "customers-agreement", "partner-deal", "suppliers")
assign("A5-R2C5", "ref-people", "customers-growth", "partner-growth")
assign("A3-R4C4", "ref-people", "customers-portfolio")
assign("A5-R1C2", "ref-people", "customers-support", "emp-rating", "sup-verified", "svc-verified")
assign("A4-R3C3", "ref-people", "emp-id", "sup-contact")
assign("A5-R1C3", "ref-people", "emp-schedule")
assign("A4-R4C1", "ref-people", "equipment", "svc-tools")
assign("A4-R1C4", "ref-people", "exchange-rate", "sarraf-rates")
assign("A5-R2C2", "ref-people", "partner-certificate")
assign("A4-R2C3", "ref-people", "partner-global")
assign("A4-R4C3", "ref-people", "sarraf-cash")
assign("A5-R3C4", "ref-people", "sarraf-transfer")
assign("A4-R4C5", "ref-people", "sarraf-vault")
assign("A2-R2C2", "ref-people", "sup-shipping")
assign("A2-R4C2", "ref-people", "svc-training")

assign("A5-R3C4", "ref-reconciliation", "recon-amount")
assign("A5-R4C4", "ref-reconciliation", "recon-audit")
assign("A5-R5C1", "ref-reconciliation", "recon-documents")
assign("A5-R5C4", "ref-reconciliation", "recon-ledger")
assign("A2-R3C4", "ref-reconciliation", "recon-operations")
assign("A2-R3C3", "ref-reconciliation", "recon-stock")

assign("A2-R4C1", "ref-reports", "rep-analysis")
assign("A3-R2C1", "ref-reports", "rep-finance-pnl")
assign("A1-R1C1", "ref-reports", "rep-inventory")
assign("A5-R3C4", "ref-reports", "rep-payments")
assign("A2-R1C1", "ref-reports", "rep-purchase")
assign("A3-R1C1", "ref-reports", "rep-sales")
assign("A5-R4C4", "ref-reports", "rep-settlements")
assign("A2-R2C2", "ref-reports", "rep-transport")

assign("A2-R1C3", "ref-shipment", "discharged")
assign("A3-R1C4", "ref-shipment", "expenses")
assign("A2-R3C1", "ref-shipment", "loaded")
assign("A3-R2C5", "ref-shipment", "loss")
assign("A3-R2C4", "ref-shipment", "losses")
assign("A3-R1C1", "ref-shipment", "profit")
assign("A2-R1C5", "ref-shipment", "remaining")
assign("A3-R4C1", "ref-shipment", "sales")


def tile_crop(atlases: dict[int, Image.Image], tile_name: str) -> Image.Image:
    match = re.fullmatch(r"A(\d)-R(\d)C(\d+)", tile_name)
    if match is None:
        raise ValueError(f"Invalid tile name: {tile_name}")

    atlas_index, row, column = map(int, match.groups())
    atlas = atlases[atlas_index]
    if atlas_index < 5:
        x_bounds = [round(index * atlas.width / 5) for index in range(6)]
        y_bounds = [round(index * atlas.height / 4) for index in range(5)]
    else:
        x_bounds = [round(index * atlas.width / 5) for index in range(6)]
        y_bounds = [0, 336, 626, 884, 1137, 1379]

    crop = atlas.crop((x_bounds[column - 1], y_bounds[row - 1], x_bounds[column], y_bounds[row]))
    side = min(crop.size)
    left = (crop.width - side) // 2
    top = (crop.height - side) // 2
    return crop.crop((left, top, left + side, top + side))


def render_avatar(source: Image.Image, size: int) -> Image.Image:
    avatar = source.resize((size, size), Image.Resampling.LANCZOS)
    radial_mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(radial_mask)
    feather = max(2, round(size * 0.025))
    draw.ellipse((feather, feather, size - feather - 1, size - feather - 1), fill=255)
    alpha = avatar.getchannel("A")
    avatar.putalpha(ImageChops.multiply(alpha, radial_mask))
    return avatar


def save_avatar(image: Image.Image, path: Path, size: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    render_avatar(image, size).save(path, "WEBP", quality=82, method=6, exact=True)


def main() -> None:
    atlases = {index: Image.open(path).convert("RGBA") for index, path in ATLAS_FILES.items()}
    base_assets = {
        path.relative_to(TARGET_DIR).with_suffix("").as_posix()
        for path in TARGET_DIR.rglob("*.webp")
        if "report-hub" not in path.parts
    }
    missing = sorted(base_assets - MAPPING.keys())
    extra = sorted(MAPPING.keys() - base_assets)
    if missing or extra:
        raise RuntimeError(f"Avatar mapping mismatch. Missing={missing}; extra={extra}")

    rendered: dict[str, Image.Image] = {}
    for relative_path, tile_name in sorted(MAPPING.items()):
        source = rendered.setdefault(tile_name, tile_crop(atlases, tile_name))
        save_avatar(source, TARGET_DIR / f"{relative_path}.webp", 256)

    report_assets = list((TARGET_DIR / "report-hub").rglob("*.webp"))
    for path in report_assets:
        relative_path = path.relative_to(TARGET_DIR / "report-hub").with_suffix("").as_posix()
        tile_name = MAPPING[relative_path]
        save_avatar(rendered[tile_name], path, 160)

    total_bytes = sum(path.stat().st_size for path in TARGET_DIR.rglob("*.webp"))
    print(f"Generated {len(base_assets)} card avatars and {len(report_assets)} hub avatars ({total_bytes / 1024 / 1024:.2f} MiB).")


if __name__ == "__main__":
    main()
