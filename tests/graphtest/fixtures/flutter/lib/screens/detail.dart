import 'package:flutter/material.dart';
import 'base_screen.dart';
import '../utils/format.dart';

class DetailScreen extends BaseScreen {
  const DetailScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return Column(children: [Text(formatPrice(3)), const HelperBanner()]);
  }
}
