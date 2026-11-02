import 'package:flutter/material.dart';
import '../widgets/product_card.dart';

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final label = 'OldBanner()';
    return GestureDetector(
      onTap: () => Navigator.pushNamed(context, '/details'),
      child: const ProductCard(),
    );
  }
}
