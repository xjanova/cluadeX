<script>
  let { city, temp, unit } = $props()

  let warm = $derived(city.temp >= 25)
</script>

<article class="card" class:warm>
  <div class="card-top">
    <h2 class="city">{city.name}</h2>
    <span class="sky">{city.sky}</span>
  </div>
  <p class="temp">{temp}°{unit}</p>
  <div class="bar"><span style="width: {city.humidity}%"></span></div>
  <p class="meta">Humidity {city.humidity}%</p>
</article>

<style>
  .card { background: var(--panel); border: 1px solid var(--line); border-radius: 20px; padding: 22px; transition: transform 0.2s; }
  .card:hover { transform: translateY(-3px); }
  .card.warm { background: linear-gradient(160deg, #fff7ed, var(--panel)); }
  .card-top { display: flex; justify-content: space-between; align-items: center; }
  .city { font-size: 18px; margin: 0; }
  .sky { font-size: 28px; }
  .temp { font-size: 44px; font-weight: 800; letter-spacing: -0.03em; margin: 12px 0; }
  .bar { height: 6px; border-radius: 6px; background: var(--line); overflow: hidden; }
  .bar span { display: block; height: 100%; background: var(--primary); }
  .meta { color: var(--muted); font-size: 13px; margin: 8px 0 0; }
</style>
