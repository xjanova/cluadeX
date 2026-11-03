from django.urls import include, path
from app import views

urlpatterns = [
    path('', views.index),
    path('api/', include('app.api.urls')),
]
