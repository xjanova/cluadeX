<?php

namespace App\Providers;

use App\Contracts\PaymentGateway;
use App\Services\StripeGateway;

class AppServiceProvider
{
    public function register()
    {
        $this->app->bind(PaymentGateway::class, StripeGateway::class);
    }
}
