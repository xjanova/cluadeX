"""Shipping charges."""

FREE_SHIPPING_OVER = 100


def shipping_cost(subtotal, express=False):
    if subtotal >= FREE_SHIPPING_OVER and not express:
        return 0
    return 15 if express else 5
