from .models import User
from app.services import mailer
# from app import legacy
import helpers

NOTE = "import app.legacy"
DOC = '''
from app import legacy
'''


def index(request):
    mailer.send("x")
    helpers.shout()
    return User.objects
