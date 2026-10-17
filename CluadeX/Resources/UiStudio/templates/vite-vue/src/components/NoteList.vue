<script setup>
defineProps({
  notes: { type: Array, required: true },
  activeId: { type: Number, default: null },
})
defineEmits(['select', 'remove'])

const when = (t) => new Date(t).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
</script>

<template>
  <ul class="list">
    <li
      v-for="note in notes"
      :key="note.id"
      class="item"
      :class="{ active: note.id === activeId }"
      @click="$emit('select', note.id)"
    >
      <div class="item-text">
        <p class="item-title">{{ note.title || 'Untitled' }}</p>
        <p class="item-sub">{{ when(note.updated) }} · {{ note.body.slice(0, 40) || 'Empty' }}</p>
      </div>
      <button class="item-del" title="Delete" @click.stop="$emit('remove', note.id)">✕</button>
    </li>
    <li v-if="notes.length === 0" class="none">No notes match.</li>
  </ul>
</template>
