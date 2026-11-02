<?php

namespace App\Services;

class ReportService
{
    public function make()
    {
        return new Invoice();
    }
}
