<script setup>
import { computed, ref, watch } from 'vue'
import NoteList from './components/NoteList.vue'
import NoteEditor from './components/NoteEditor.vue'

function load() {
  try {
    const raw = JSON.parse(localStorage.getItem('notes') || 'null')
    if (Array.isArray(raw) && raw.length) return raw
  } catch { /* fall through to the welcome note */ }
  return [{ id: 1, title: 'Welcome 👋', body: 'Click any element in CluadeX UI Studio to edit it.\nThis note is saved in your browser.', updated: Date.now() }]
}

const notes = ref(load())
const activeId = ref(notes.value[0]?.id ?? null)
const query = ref('')

watch(notes, (v) => localStorage.setItem('notes', JSON.stringify(v)), { deep: true })

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase()
  return notes.value
    .filter((n) => !q || (n.title + ' ' + n.body).toLowerCase().includes(q))
    .sort((a, b) => b.updated - a.updated)
})
const active = computed(() => notes.value.find((n) => n.id === activeId.value) || null)

function addNote() {
  const note = { id: Date.now(), title: 'Untitled', body: '', updated: Date.now() }
  notes.value.push(note)
  activeId.value = note.id
}
function removeNote(id) {
  notes.value = notes.value.filter((n) => n.id !== id)
  if (activeId.value === id) activeId.value = notes.value[0]?.id ?? null
}
function update(patch) {
  if (!active.value) return
  Object.assign(active.value, patch, { updated: Date.now() })
}
</script>

<template>
  <div class="app">
    <aside class="sidebar">
      <div class="side-head">
        <h1 class="logo">{{PROJECT_TITLE}}</h1>
        <button class="btn btn-primary" @click="addNote">+ New</button>
      </div>
      <input v-model="query" class="search" type="search" placeholder="Search notes…" />
      <NoteList :notes="filtered" :active-id="activeId" @select="activeId = $event" @remove="removeNote" />
    </aside>
    <main class="main">
      <NoteEditor v-if="active" :note="active" @update="update" />
      <div v-else class="empty">
        <p class="empty-title">No note selected</p>
        <button class="btn btn-primary" @click="addNote">Create a note</button>
      </div>
    </main>
  </div>
</template>
