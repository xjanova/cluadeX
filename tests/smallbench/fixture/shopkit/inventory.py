"""Stock levels per SKU."""
from shopkit.audit import log_audit

MAX_STOCK = 500


class OutOfStockError(Exception):
    """Raised when more units are taken than are in stock."""


class Inventory:
    def __init__(self):
        self.stock = {}

    def restock(self, sku, qty):
        new_level = self.stock.get(sku, 0) + qty
        if new_level > MAX_STOCK:
            raise ValueError(f"{sku}: stock {new_level} would exceed MAX_STOCK {MAX_STOCK}")
        self.stock[sku] = new_level
        log_audit(f"restock {sku} +{qty}")

    def take(self, sku, qty):
        have = self.stock.get(sku, 0)
        if qty > have:
            raise OutOfStockError(f"{sku}: wanted {qty}, have {have}")
        self.stock[sku] = have - qty
        log_audit(f"take {sku} -{qty}")
