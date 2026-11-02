import 'package:flutter/material.dart';
import 'package:shop/routes.dart';
import 'services/cart_service.dart';
// import 'widgets/old_banner.dart';

final getIt = GetIt.instance;

void main() {
  getIt.registerLazySingleton<CartService>(() => CartService());
  runApp(MaterialApp(routes: appRoutes));
}
