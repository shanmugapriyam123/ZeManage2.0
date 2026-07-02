"""
Generates docs/


-Used-Endpoints.xlsx from the CSV with proper
column widths, frozen header, and color-coded HTTP verbs.

Run from repo root:
    python docs/API/generate_used_endpoints_xlsx.py
"""

import csv
import os
import sys
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment, Border, Side
from openpyxl.utils import get_column_letter

HERE = os.path.dirname(os.path.abspath(__file__))
CSV_PATH = os.path.join(HERE, "ZeManage-Used-Endpoints.csv")
XLSX_PATH = os.path.join(HERE, "ZeManage-Used-Endpoints.xlsx")

VERB_FILL = {
    "GET":    PatternFill(start_color="DBEAFE", end_color="DBEAFE", fill_type="solid"),  # blue-100
    "POST":   PatternFill(start_color="DCFCE7", end_color="DCFCE7", fill_type="solid"),  # green-100
    "PUT":    PatternFill(start_color="FEF3C7", end_color="FEF3C7", fill_type="solid"),  # amber-100
    "PATCH":  PatternFill(start_color="E0E7FF", end_color="E0E7FF", fill_type="solid"),  # indigo-100
    "DELETE": PatternFill(start_color="FEE2E2", end_color="FEE2E2", fill_type="solid"),  # red-100
}
VERB_FONT = {
    "GET":    Font(color="1E40AF", bold=True),
    "POST":   Font(color="166534", bold=True),
    "PUT":    Font(color="92400E", bold=True),
    "PATCH":  Font(color="3730A3", bold=True),
    "DELETE": Font(color="991B1B", bold=True),
}

HEADER_FILL = PatternFill(start_color="002A54", end_color="002A54", fill_type="solid")
HEADER_FONT = Font(color="FFFFFF", bold=True, size=11)
HEADER_ALIGN = Alignment(horizontal="center", vertical="center")

THIN = Side(border_style="thin", color="E2E8F0")
BORDER = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)


def main():
    if not os.path.exists(CSV_PATH):
        print(f"CSV not found: {CSV_PATH}", file=sys.stderr)
        sys.exit(1)

    wb = Workbook()
    ws = wb.active
    ws.title = "Used Endpoints"

    with open(CSV_PATH, newline="", encoding="utf-8") as f:
        rows = list(csv.reader(f))

    # Write header
    header = rows[0]
    for col_idx, value in enumerate(header, start=1):
        cell = ws.cell(row=1, column=col_idx, value=value)
        cell.fill = HEADER_FILL
        cell.font = HEADER_FONT
        cell.alignment = HEADER_ALIGN
        cell.border = BORDER

    # Write data rows
    for row_idx, row in enumerate(rows[1:], start=2):
        for col_idx, value in enumerate(row, start=1):
            cell = ws.cell(row=row_idx, column=col_idx, value=value)
            cell.border = BORDER
            cell.alignment = Alignment(vertical="center", wrap_text=False)

        # Color the verb cell (column B = 2)
        verb = row[1].strip().upper() if len(row) > 1 else ""
        if verb in VERB_FILL:
            verb_cell = ws.cell(row=row_idx, column=2)
            verb_cell.fill = VERB_FILL[verb]
            verb_cell.font = VERB_FONT[verb]
            verb_cell.alignment = Alignment(horizontal="center", vertical="center")

    # Auto-fit column widths based on max content length per column
    for col_idx, _ in enumerate(header, start=1):
        max_len = 0
        for row in rows:
            if col_idx - 1 < len(row):
                cell_value = row[col_idx - 1] or ""
                max_len = max(max_len, len(str(cell_value)))
        # Padding + cap
        ws.column_dimensions[get_column_letter(col_idx)].width = min(max_len + 3, 80)

    # Freeze the header row
    ws.freeze_panes = "A2"

    # Light row banding for readability
    band_fill = PatternFill(start_color="F8FAFC", end_color="F8FAFC", fill_type="solid")
    for row_idx in range(2, len(rows) + 1):
        if row_idx % 2 == 0:
            for col_idx in range(1, len(header) + 1):
                cell = ws.cell(row=row_idx, column=col_idx)
                # Don't overwrite the colored verb cell
                if col_idx == 2:
                    continue
                cell.fill = band_fill

    # Autofilter on the data range
    ws.auto_filter.ref = f"A1:{get_column_letter(len(header))}{len(rows)}"

    wb.save(XLSX_PATH)
    print(f"Wrote {XLSX_PATH}")


if __name__ == "__main__":
    main()
