"""Pricing rules for the shop."""

TAX_RATE = 0.07
MAX_DISCOUNT_PCT = 50


def with_tax(price):
    """Price including tax, rounded to cents."""
    return round(price * (1 + TAX_RATE), 2)


def apply_discount(price, pct):
    """Return the price after a percentage discount. Discounts above MAX_DISCOUNT_PCT are capped."""
    if pct < 0:
        raise ValueError("discount cannot be negative")
    pct = min(pct, MAX_DISCOUNT_PCT)
    return round(price * (100 - pct) / 100, 2)
