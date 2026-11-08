"""An in-memory audit trail."""
import datetime

_LOG = []


def log_audit(event):
    """Record one event with the time it happened."""
    _LOG.append((datetime.datetime.now().isoformat(), event))


def entries():
    """Every recorded event, oldest first, as (timestamp, event) pairs."""
    return list(_LOG)
