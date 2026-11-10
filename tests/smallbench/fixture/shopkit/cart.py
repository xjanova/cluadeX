"""A shopping cart."""
from shopkit import pricing
from shopkit.audit import log_audit


class Cart:
    def __init__(self, discount_pct=0):
        self.items = []
        self.discount_pct = discount_pct

    def add_item(self, name, price, qty=1):
        if qty <= 0:
            raise ValueError("qty must be positive")
        self.items.append((name, price, qty))
        log_audit(f"cart.add {name} x{qty}")

    def subtotal(self):
        return sum(price * qty for _, price, qty in self.items)

    def total(self):
        discounted = pricing.applyDiscount(self.subtotal(), self.discount_pct)
        return pricing.with_tax(discounted)
