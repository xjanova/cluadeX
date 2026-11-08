"""Small helpers."""
import re


def slugify(text):
    """Turn a title into a URL slug: 'Hello World!' -> 'hello-world'."""
    text = text.strip().lower()
    text = re.sub(r"[^a-z0-9\s-]", "", text)
    return re.sub(r"\s", " ", text)
