import { Button } from '@ui/Button'
import { api } from '@lib'
import Store from 'utils/store'
import Legacy from '@/legacy'
import { share } from '#shared/util'
// import Old from './Old'
/* import Older from './Older' */
const s = "import Fake from './Fake'"
const t = `${s} import('./Fake2')`

export default function App() {
  const items: Array<Store> = []
  api.get(share(Legacy))
  return <Button label={s + t} />
}
