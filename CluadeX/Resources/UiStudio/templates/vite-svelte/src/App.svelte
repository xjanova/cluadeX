<script>
  import CityCard from './lib/CityCard.svelte'

  let unit = $state('C')
  let query = $state('')

  const cities = [
    { name: 'Bangkok', temp: 33, sky: '☀️', humidity: 70 },
    { name: 'Chiang Mai', temp: 27, sky: '⛅', humidity: 62 },
    { name: 'Tokyo', temp: 18, sky: '🌧️', humidity: 81 },
    { name: 'London', temp: 11, sky: '☁️', humidity: 77 },
    { name: 'New York', temp: 15, sky: '🌤️', humidity: 58 },
    { name: 'Sydney', temp: 22, sky: '☀️', humidity: 49 },
  ]

  let shown = $derived(cities.filter((c) => c.name.toLowerCase().includes(query.trim().toLowerCase())))
  let average = $derived(shown.length ? Math.round(shown.reduce((s, c) => s + c.temp, 0) / shown.length) : 0)
  const show = (c) => (unit === 'C' ? c : Math.round((c * 9) / 5 + 32))
</script>

<main class="page">
  <header class="head">
    <div>
      <h1 class="title">{{PROJECT_TITLE}}</h1>
      <p class="sub">Average across {shown.length} cities: <b>{show(average)}°{unit}</b></p>
    </div>
    <div class="controls">
      <input class="search" type="search" placeholder="Find a city…" bind:value={query} />
      <div class="units" role="group" aria-label="Units">
        <button class:active={unit === 'C'} onclick={() => (unit = 'C')}>°C</button>
        <button class:active={unit === 'F'} onclick={() => (unit = 'F')}>°F</button>
      </div>
    </div>
  </header>

  {#if shown.length}
    <section class="grid">
      {#each shown as city (city.name)}
        <CityCard {city} temp={show(city.temp)} {unit} />
      {/each}
    </section>
  {:else}
    <p class="empty">No city matches “{query}”.</p>
  {/if}
</main>
