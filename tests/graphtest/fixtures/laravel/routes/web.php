<?php

use App\Http\Controllers\UserController;
use Illuminate\Support\Facades\Route;

Route::get('/users', [UserController::class, 'index']);
Route::view('/about', 'about');
// Route::get('/old', [OldController::class, 'show']);
$note = "Route::get('/fake', [OldController::class, 'show'])";
